namespace Ryujinx.HLE.HOS.Tamper.Operations
{
    class OpMov<T> : IOperation where T : unmanaged
    {
        readonly IOperand _destination;
        readonly IOperand _source;

        // 添加无参数构造函数
        public OpMov()
        {
            // 可以留空或设置默认值
        }

        public OpMov(IOperand destination, IOperand source)
        {
            _destination = destination;
            _source = source;
        }

        public void Execute()
        {
            _destination.Set(_source.Get<T>());
        }
    }
}
