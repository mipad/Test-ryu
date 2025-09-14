using Ryujinx.Common.Logging;

namespace Ryujinx.HLE.HOS.Tamper.Operations
{
    class OpMov<T> : IOperation where T : unmanaged
    {
        readonly IOperand _destination;
        readonly IOperand _source;

        // 添加无参数构造函数
        public OpMov()
        {
            Logger.Debug?.Print(LogClass.TamperMachine, $"OpMov<{typeof(T).Name}> no-arg constructor called");
        }

        public OpMov(IOperand destination, IOperand source)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, $"OpMov<{typeof(T).Name}> two-arg constructor called with {destination} and {source}");
            _destination = destination;
            _source = source;
        }

        public void Execute()
        {
            Logger.Debug?.Print(LogClass.TamperMachine, $"OpMov<{typeof(T).Name}> executing");
            _destination.Set(_source.Get<T>());
        }
    }
}
