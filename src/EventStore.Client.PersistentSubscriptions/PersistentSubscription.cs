using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.PersistentSubscriptions;
using Grpc.Core;

#nullable enable
namespace EventStore.Client {
	public class PersistentSubscription : IDisposable {
		private readonly bool _autoAck;
		private readonly Func<PersistentSubscription, ResolvedEvent, int?, CancellationToken, Task> _eventAppeared;
		private readonly Action<PersistentSubscription, SubscriptionDroppedReason, Exception?> _subscriptionDropped;
		private readonly CancellationTokenSource _disposed;
		private readonly AsyncDuplexStreamingCall<ReadReq, ReadResp> _call;
		private int _subscriptionDroppedInvoked;

		public string SubscriptionId { get; }

		internal static async Task<PersistentSubscription> Confirm(AsyncDuplexStreamingCall<ReadReq, ReadResp> call,
			ReadReq.Types.Options options, bool autoAck,
			Func<PersistentSubscription, ResolvedEvent, int?, CancellationToken, Task> eventAppeared,
			Action<PersistentSubscription, SubscriptionDroppedReason, Exception?> subscriptionDropped,
			CancellationToken cancellationToken = default) {
			await call.RequestStream.WriteAsync(new ReadReq {
				Options = options
			}).ConfigureAwait(false);

			if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false) ||
			    call.ResponseStream.Current.ContentCase != ReadResp.ContentOneofCase.SubscriptionConfirmation) {
				throw new InvalidOperationException();
			}

			return new PersistentSubscription(call, autoAck, eventAppeared, subscriptionDropped);
		}

		private PersistentSubscription(
			AsyncDuplexStreamingCall<ReadReq, ReadResp> call,
			bool autoAck,
			Func<PersistentSubscription, ResolvedEvent, int?, CancellationToken, Task> eventAppeared,
			Action<PersistentSubscription, SubscriptionDroppedReason, Exception?> subscriptionDropped) {
			_call = call;
			_autoAck = autoAck;
			_eventAppeared = eventAppeared;
			_subscriptionDropped = subscriptionDropped;
			_disposed = new CancellationTokenSource();
			SubscriptionId = call.ResponseStream.Current.SubscriptionConfirmation.SubscriptionId;
			Task.Run(Subscribe);
		}

		public Task Ack(params Uuid[] eventIds) {
			if (eventIds.Length > 2000) {
				throw new ArgumentException();
			}

			return AckInternal(eventIds);
		}

		public Task Ack(IEnumerable<Uuid> eventIds) => Ack(eventIds.ToArray());

		public Task Ack(params ResolvedEvent[] resolvedEvents) =>
			Ack(Array.ConvertAll(resolvedEvents, resolvedEvent => resolvedEvent.OriginalEvent.EventId));

		public Task Ack(IEnumerable<ResolvedEvent> resolvedEvents) =>
			Ack(resolvedEvents.Select(resolvedEvent => resolvedEvent.OriginalEvent.EventId));

		public Task Nack(PersistentSubscriptionNakEventAction action, string reason, params Uuid[] eventIds) {
			if (eventIds.Length > 2000) {
				throw new ArgumentException();
			}

			return NackInternal(eventIds, action, reason);
		}

		public Task Nack(PersistentSubscriptionNakEventAction action, string reason,
			params ResolvedEvent[] resolvedEvents) =>
			Nack(action, reason,
				Array.ConvertAll(resolvedEvents, resolvedEvent => resolvedEvent.OriginalEvent.EventId));

		public void Dispose() {
			if (_disposed.IsCancellationRequested) {
				return;
			}

			SubscriptionDropped(SubscriptionDroppedReason.Disposed);

			_disposed.Dispose();
		}

		private async Task Subscribe() {
			try {
				while (await _call!.ResponseStream.MoveNext().ConfigureAwait(false) &&
				       !_disposed.IsCancellationRequested) {
					var current = _call!.ResponseStream.Current;
					switch (current.ContentCase) {
						case ReadResp.ContentOneofCase.Event:
							try {
								await _eventAppeared(this, ConvertToResolvedEvent(current),
									current.Event.CountCase switch {
										ReadResp.Types.ReadEvent.CountOneofCase.RetryCount => current.Event.RetryCount,
										_ => default
									}, _disposed.Token).ConfigureAwait(false);
								if (_autoAck) {
									await AckInternal(Uuid.FromDto(current.Event.Link?.Id ?? current.Event.Event.Id))
										.ConfigureAwait(false);
								}
							} catch (Exception ex) when (ex is ObjectDisposedException ||
							                             ex is OperationCanceledException) {
								SubscriptionDropped(SubscriptionDroppedReason.Disposed);
								return;
							} catch (Exception ex) {
								try {
									SubscriptionDropped(SubscriptionDroppedReason.SubscriberError, ex);
								} finally {
									_disposed.Cancel();
								}

								return;
							}

							break;
					}
				}
			} catch (Exception ex) {
				try {
					SubscriptionDropped(SubscriptionDroppedReason.ServerError, ex);
				} finally {
					_disposed.Cancel();
				}
			}

			ResolvedEvent ConvertToResolvedEvent(ReadResp response) =>
				new ResolvedEvent(
					ConvertToEventRecord(response.Event.Event)!,
					ConvertToEventRecord(response.Event.Link),
					response.Event.PositionCase switch {
						ReadResp.Types.ReadEvent.PositionOneofCase.CommitPosition => response.Event.CommitPosition,
						ReadResp.Types.ReadEvent.PositionOneofCase.NoPosition => null,
						_ => throw new InvalidOperationException()
					});

			EventRecord? ConvertToEventRecord(ReadResp.Types.ReadEvent.Types.RecordedEvent e) =>
				e == null
					? null
					: new EventRecord(
						e.StreamIdentifier,
						Uuid.FromDto(e.Id),
						new StreamPosition(e.StreamRevision),
						new Position(e.CommitPosition, e.PreparePosition),
						e.Metadata,
						e.Data.ToByteArray(),
						e.CustomMetadata.ToByteArray());
		}

		private void SubscriptionDropped(SubscriptionDroppedReason reason, Exception? ex = null) {
			if (Interlocked.CompareExchange(ref _subscriptionDroppedInvoked, 1, 0) == 1) {
				return;
			}

			_call?.Dispose();
			_subscriptionDropped?.Invoke(this, reason, ex);
			_disposed.Dispose();
		}

		private Task AckInternal(params Uuid[] ids) =>
			_call!.RequestStream.WriteAsync(new ReadReq {
				Ack = new ReadReq.Types.Ack {
					Ids = {
						Array.ConvertAll(ids, id => id.ToDto())
					}
				}
			});

		private Task NackInternal(Uuid[] ids, PersistentSubscriptionNakEventAction action, string reason) =>
			_call!.RequestStream.WriteAsync(new ReadReq {
				Nack = new ReadReq.Types.Nack {
					Ids = {
						Array.ConvertAll(ids, id => id.ToDto())
					},
					Action = action switch {
						PersistentSubscriptionNakEventAction.Park => ReadReq.Types.Nack.Types.Action.Park,
						PersistentSubscriptionNakEventAction.Retry => ReadReq.Types.Nack.Types.Action.Retry,
						PersistentSubscriptionNakEventAction.Skip => ReadReq.Types.Nack.Types.Action.Skip,
						PersistentSubscriptionNakEventAction.Stop => ReadReq.Types.Nack.Types.Action.Stop,
						PersistentSubscriptionNakEventAction.Unknown => ReadReq.Types.Nack.Types.Action.Unknown,
						_ => throw new ArgumentOutOfRangeException(nameof(action))
					},
					Reason = reason
				}
			});
	}
}
