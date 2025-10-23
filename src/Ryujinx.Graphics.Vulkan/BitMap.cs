namespace Ryujinx.Graphics.Vulkan
{
    readonly struct BitMap
    {
        public const int IntSize = 64;

        private const int IntShift = 6;
        private const int IntMask = IntSize - 1;

        private readonly long[] _masks;

        public BitMap(int count)
        {
            if (count < 0) count = 0;
            _masks = new long[(count + IntMask) / IntSize];
        }

        private int WordCount => _masks?.Length ?? 0;
        private int MaxBit => (WordCount * IntSize) - 1;

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        public bool AnySet()
        {
            for (int i = 0; i < WordCount; i++)
            {
                if (_masks[i] != 0)
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsSet(int bit)
        {
            if (WordCount == 0) return false;
            bit = Clamp(bit, 0, MaxBit);

            int wordIndex = bit >> IntShift;
            int wordBit = bit & IntMask;

            long wordMask = 1L << wordBit;

            return (_masks[wordIndex] & wordMask) != 0;
        }

        public bool IsSet(int start, int end)
        {
            if (WordCount == 0) return false;

            if (start > end) { var t = start; start = end; end = t; }
            start = Clamp(start, 0, MaxBit);
            end = Clamp(end, 0, MaxBit);

            if (start == end)
            {
                return IsSet(start);
            }

            int startIndex = start >> IntShift;
            int startBit = start & IntMask;
            long startMask = -1L << startBit;

            int endIndex = end >> IntShift;
            int endBit = end & IntMask;
            long endMask = (long)(ulong.MaxValue >> (IntMask - endBit));

            if (startIndex == endIndex)
            {
                return (_masks[startIndex] & startMask & endMask) != 0;
            }

            if ((_masks[startIndex] & startMask) != 0)
            {
                return true;
            }

            for (int i = startIndex + 1; i < endIndex && i < WordCount; i++)
            {
                if (_masks[i] != 0)
                {
                    return true;
                }
            }

            if (endIndex < WordCount && (_masks[endIndex] & endMask) != 0)
            {
                return true;
            }

            return false;
        }

        public bool Set(int bit)
        {
            if (WordCount == 0) return false;
            bit = Clamp(bit, 0, MaxBit);

            int wordIndex = bit >> IntShift;
            int wordBit = bit & IntMask;

            long wordMask = 1L << wordBit;

            if ((_masks[wordIndex] & wordMask) != 0)
            {
                return false;
            }

            _masks[wordIndex] |= wordMask;

            return true;
        }

        public void SetRange(int start, int end)
        {
            if (WordCount == 0) return;

            if (start > end) { var t = start; start = end; end = t; }
            start = Clamp(start, 0, MaxBit);
            end = Clamp(end, 0, MaxBit);

            if (start == end)
            {
                Set(start);
                return;
            }

            int startIndex = start >> IntShift;
            int startBit = start & IntMask;
            long startMask = -1L << startBit;

            int endIndex = end >> IntShift;
            int endBit = end & IntMask;
            long endMask = (long)(ulong.MaxValue >> (IntMask - endBit));

            if (startIndex == endIndex)
            {
                _masks[startIndex] |= startMask & endMask;
            }
            else
            {
                _masks[startIndex] |= startMask;

                for (int i = startIndex + 1; i < endIndex && i < WordCount; i++)
                {
                    _masks[i] |= -1;
                }

                if (endIndex < WordCount)
                {
                    _masks[endIndex] |= endMask;
                }
            }
        }

        public void Clear(int bit)
        {
            if (WordCount == 0) return;
            bit = Clamp(bit, 0, MaxBit);

            int wordIndex = bit >> IntShift;
            int wordBit = bit & IntMask;

            long wordMask = 1L << wordBit;

            _masks[wordIndex] &= ~wordMask;
        }

        public void Clear()
        {
            for (int i = 0; i < WordCount; i++)
            {
                _masks[i] = 0;
            }
        }

        public void ClearInt(int start, int end)
        {
            if (WordCount == 0) return;
            if (start > end) { var t = start; start = end; end = t; }

            start = Clamp(start, 0, WordCount - 1);
            end = Clamp(end, 0, WordCount - 1);

            for (int i = start; i <= end; i++)
            {
                _masks[i] = 0;
            }
        }
    }
}
