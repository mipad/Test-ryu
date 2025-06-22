return Status.InvalidOperation;
                }

                if (Core.BufferHasBeenQueued)
                {
                    int newUndequeuedCount = maxBufferCount - (dequeuedCount + 1);
                    int minUndequeuedCount = Core.GetMinUndequeuedBufferCountLocked(async);

                    if (newUndequeuedCount < minUndequeuedCount)
                    {
                        Logger.Error?.Print(LogClass.SurfaceFlinger, $"Min undequeued buffer count ({minUndequeuedCount}) exceeded (dequeued = {dequeuedCount} undequeued = {newUndequeuedCount})");

                        return Status.InvalidOperation;
                    }
                }

                bool tooManyBuffers = Core.Queue.Count > maxBufferCount;

                tryAgain = freeSlot == BufferSlotArray.InvalidBufferSlot || tooManyBuffers;

                if (tryAgain)
                {
                    if (async || (Core.DequeueBufferCannotBlock && acquiredCount < Core.MaxAcquiredBufferCount))
                    {
                        Core.CheckSystemEventsLocked(maxBufferCount);

                        return Status.WouldBlock;
                    }

                    Core.WaitDequeueEvent();

                    if (!Core.Active)
                    {
                        break;
                    }
                }
            }

            return Status.Success;
        }

        protected override KReadableEvent GetWaitBufferFreeEvent()
        {
            return Core.GetWaitBufferFreeEvent();
        }

        public override Status GetBufferHistory(int bufferHistoryCount, out Span<BufferInfo> bufferInfos)
        {
            if (bufferHistoryCount <= 0)
            {
                bufferInfos = Span<BufferInfo>.Empty;

                return Status.BadValue;
            }

            lock (Core.Lock)
            {
                bufferHistoryCount = Math.Min(bufferHistoryCount, Core.BufferHistory.Length);

                BufferInfo[] result = new BufferInfo[bufferHistoryCount];

                uint position = Core.BufferHistoryPosition;

                for (uint i = 0; i < bufferHistoryCount; i++)
                {
                    result[i] = Core.BufferHistory[(position - i) % Core.BufferHistory.Length];

                    position--;
                }

                bufferInfos = result;

                return Status.Success;
            }
        }
    }
}
