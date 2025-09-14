using Ryujinx.HLE.Exceptions;
using Ryujinx.HLE.HOS.Tamper.Conditions;
using Ryujinx.HLE.HOS.Tamper.Operations;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ryujinx.HLE.HOS.Tamper
{
    class InstructionHelper
    {
        private const int CodeTypeIndex = 0;
        
        // 预定义类型创建委托
        private static readonly Dictionary<Type, Func<byte, object[], object>> _typeFactories = new Dictionary<Type, Func<byte, object[], object>>();
        
        static InstructionHelper()
        {
            InitializeFactories();
        }
        
        private static void InitializeFactories()
        {
            // 为每个类型注册工厂方法
            RegisterFactory(typeof(OpMov<>), (width, operands) => 
            {
                if (operands.Length < 2)
                    throw new TamperCompilationException("OpMov requires at least 2 operands");
                
                IOperand destination = (IOperand)operands[0];
                IOperand source = (IOperand)operands[1];
                
                return width switch
                {
                    1 => new OpMov<byte>(destination, source),
                    2 => new OpMov<ushort>(destination, source),
                    4 => new OpMov<uint>(destination, source),
                    8 => new OpMov<ulong>(destination, source),
                    _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
                };
            });
            
            // 条件类型的工厂方法
            RegisterConditionFactory(typeof(CondGT<>));
            RegisterConditionFactory(typeof(CondGE<>));
            RegisterConditionFactory(typeof(CondLT<>));
            RegisterConditionFactory(typeof(CondLE<>));
            RegisterConditionFactory(typeof(CondEQ<>));
            RegisterConditionFactory(typeof(CondNE<>));
            
            // 其他操作类型的工厂方法
            RegisterOperationFactory(typeof(OpAdd<>));
            RegisterOperationFactory(typeof(OpSub<>));
            RegisterOperationFactory(typeof(OpMul<>));
            RegisterOperationFactory(typeof(OpDiv<>));
            RegisterOperationFactory(typeof(OpAnd<>));
            RegisterOperationFactory(typeof(OpOr<>));
            RegisterOperationFactory(typeof(OpXor<>));
            RegisterOperationFactory(typeof(OpNot<>));
            RegisterOperationFactory(typeof(OpLsh<>));
            RegisterOperationFactory(typeof(OpRsh<>));
        }
        
        private static void RegisterConditionFactory(Type conditionType)
        {
            _typeFactories[conditionType] = (width, operands) =>
            {
                if (operands.Length < 2)
                    throw new TamperCompilationException("Condition requires at least 2 operands");
                
                IOperand lhs = (IOperand)operands[0];
                IOperand rhs = (IOperand)operands[1];
                
                return width switch
                {
                    1 => conditionType == typeof(CondGT<>) ? new CondGT<byte>(lhs, rhs) :
                          conditionType == typeof(CondGE<>) ? new CondGE<byte>(lhs, rhs) :
                          conditionType == typeof(CondLT<>) ? new CondLT<byte>(lhs, rhs) :
                          conditionType == typeof(CondLE<>) ? new CondLE<byte>(lhs, rhs) :
                          conditionType == typeof(CondEQ<>) ? new CondEQ<byte>(lhs, rhs) :
                          conditionType == typeof(CondNE<>) ? new CondNE<byte>(lhs, rhs) :
                          throw new TamperCompilationException($"Unsupported condition type {conditionType}"),
                    2 => conditionType == typeof(CondGT<>) ? new CondGT<ushort>(lhs, rhs) :
                          conditionType == typeof(CondGE<>) ? new CondGE<ushort>(lhs, rhs) :
                          conditionType == typeof(CondLT<>) ? new CondLT<ushort>(lhs, rhs) :
                          conditionType == typeof(CondLE<>) ? new CondLE<ushort>(lhs, rhs) :
                          conditionType == typeof(CondEQ<>) ? new CondEQ<ushort>(lhs, rhs) :
                          conditionType == typeof(CondNE<>) ? new CondNE<ushort>(lhs, rhs) :
                          throw new TamperCompilationException($"Unsupported condition type {conditionType}"),
                    4 => conditionType == typeof(CondGT<>) ? new CondGT<uint>(lhs, rhs) :
                          conditionType == typeof(CondGE<>) ? new CondGE<uint>(lhs, rhs) :
                          conditionType == typeof(CondLT<>) ? new CondLT<uint>(lhs, rhs) :
                          conditionType == typeof(CondLE<>) ? new CondLE<uint>(lhs, rhs) :
                          conditionType == typeof(CondEQ<>) ? new CondEQ<uint>(lhs, rhs) :
                          conditionType == typeof(CondNE<>) ? new CondNE<uint>(lhs, rhs) :
                          throw new TamperCompilationException($"Unsupported condition type {conditionType}"),
                    8 => conditionType == typeof(CondGT<>) ? new CondGT<ulong>(lhs, rhs) :
                          conditionType == typeof(CondGE<>) ? new CondGE<ulong>(lhs, rhs) :
                          conditionType == typeof(CondLT<>) ? new CondLT<ulong>(lhs, rhs) :
                          conditionType == typeof(CondLE<>) ? new CondLE<ulong>(lhs, rhs) :
                          conditionType == typeof(CondEQ<>) ? new CondEQ<ulong>(lhs, rhs) :
                          conditionType == typeof(CondNE<>) ? new CondNE<ulong>(lhs, rhs) :
                          throw new TamperCompilationException($"Unsupported condition type {conditionType}"),
                    _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
                };
            };
        }
        
        private static void RegisterOperationFactory(Type operationType)
        {
            _typeFactories[operationType] = (width, operands) =>
            {
                return width switch
                {
                    1 => operationType == typeof(OpAdd<>) ? new OpAdd<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpSub<>) ? new OpSub<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpMul<>) ? new OpMul<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpDiv<>) ? new OpDiv<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpAnd<>) ? new OpAnd<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpOr<>) ? new OpOr<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpXor<>) ? new OpXor<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpNot<>) ? new OpNot<byte>((IOperand)operands[0], (IOperand)operands[1]) :
                          operationType == typeof(OpLsh<>) ? new OpLsh<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpRsh<>) ? new OpRsh<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          throw new TamperCompilationException($"Unsupported operation type {operationType}"),
                    2 => operationType == typeof(OpAdd<>) ? new OpAdd<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpSub<>) ? new OpSub<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpMul<>) ? new OpMul<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpDiv<>) ? new OpDiv<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpAnd<>) ? new OpAnd<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpOr<>) ? new OpOr<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpXor<>) ? new OpXor<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpNot<>) ? new OpNot<ushort>((IOperand)operands[0], (IOperand)operands[1]) :
                          operationType == typeof(OpLsh<>) ? new OpLsh<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpRsh<>) ? new OpRsh<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          throw new TamperCompilationException($"Unsupported operation type {operationType}"),
                    4 => operationType == typeof(OpAdd<>) ? new OpAdd<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpSub<>) ? new OpSub<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpMul<>) ? new OpMul<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpDiv<>) ? new OpDiv<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpAnd<>) ? new OpAnd<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpOr<>) ? new OpOr<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpXor<>) ? new OpXor<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpNot<>) ? new OpNot<uint>((IOperand)operands[0], (IOperand)operands[1]) :
                          operationType == typeof(OpLsh<>) ? new OpLsh<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpRsh<>) ? new OpRsh<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          throw new TamperCompilationException($"Unsupported operation type {operationType}"),
                    8 => operationType == typeof(OpAdd<>) ? new OpAdd<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpSub<>) ? new OpSub<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpMul<>) ? new OpMul<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpDiv<>) ? new OpDiv<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpAnd<>) ? new OpAnd<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpOr<>) ? new OpOr<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpXor<>) ? new OpXor<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpNot<>) ? new OpNot<ulong>((IOperand)operands[0], (IOperand)operands[1]) :
                          operationType == typeof(OpLsh<>) ? new OpLsh<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          operationType == typeof(OpRsh<>) ? new OpRsh<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                          throw new TamperCompilationException($"Unsupported operation type {operationType}"),
                    _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
                };
            };
        }
        
        private static void RegisterFactory(Type genericType, Func<byte, object[], object> factory)
        {
            _typeFactories[genericType] = factory;
        }

        public static void Emit(IOperation operation, CompilationContext context)
        {
            context.CurrentOperations.Add(operation);
        }

        public static void Emit(Type instruction, byte width, CompilationContext context, params Object[] operands)
        {
            Emit((IOperation)Create(instruction, width, operands), context);
        }

        public static void EmitMov(byte width, CompilationContext context, IOperand destination, IOperand source)
        {
            Emit(typeof(OpMov<>), width, context, destination, source);
        }

        public static ICondition CreateCondition(Comparison comparison, byte width, IOperand lhs, IOperand rhs)
        {
            return comparison switch
            {
                Comparison.Greater => (ICondition)Create(typeof(CondGT<>), width, lhs, rhs),
                Comparison.GreaterOrEqual => (ICondition)Create(typeof(CondGE<>), width, lhs, rhs),
                Comparison.Less => (ICondition)Create(typeof(CondLT<>), width, lhs, rhs),
                Comparison.LessOrEqual => (ICondition)Create(typeof(CondLE<>), width, lhs, rhs),
                Comparison.Equal => (ICondition)Create(typeof(CondEQ<>), width, lhs, rhs),
                Comparison.NotEqual => (ICondition)Create(typeof(CondNE<>), width, lhs, rhs),
                _ => throw new TamperCompilationException($"Invalid comparison {comparison} in Atmosphere cheat"),
            };
        }

        public static Object Create(Type instruction, byte width, params Object[] operands)
        {
            if (_typeFactories.TryGetValue(instruction, out var factory))
            {
                return factory(width, operands);
            }

            throw new TamperCompilationException($"Unsupported instruction type {instruction} with width {width} in Atmosphere cheat");
        }

        public static ulong GetImmediate(byte[] instruction, int index, int nybbleCount)
        {
            ulong value = 0;

            for (int i = 0; i < nybbleCount; i++)
            {
                value <<= 4;
                value |= instruction[index + i];
            }

            return value;
        }

        public static CodeType GetCodeType(byte[] instruction)
        {
            int codeType = instruction[CodeTypeIndex];

            if (codeType >= 0xC)
            {
                byte extension = instruction[CodeTypeIndex + 1];
                codeType = (codeType << 4) | extension;

                if (extension == 0xF)
                {
                    extension = instruction[CodeTypeIndex + 2];
                    codeType = (codeType << 4) | extension;
                }
            }

            return (CodeType)codeType;
        }

        public static byte[] ParseRawInstruction(string rawInstruction)
        {
            const int WordSize = 2 * sizeof(uint);

            var words = rawInstruction.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            byte[] instruction = new byte[WordSize * words.Length];
            
            if (words.Length == 0)
            {
                throw new TamperCompilationException("Empty instruction in Atmosphere cheat");
            }

            for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
            {
                string word = words[wordIndex];

                if (word.Length != WordSize)
                {
                    throw new TamperCompilationException($"Invalid word length for {word} in Atmosphere cheat");
                }

                for (int nybbleIndex = 0; nybbleIndex < WordSize; nybbleIndex++)
                {
                    int index = wordIndex * WordSize + nybbleIndex;

                    instruction[index] = byte.Parse(word.AsSpan(nybbleIndex, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
            }

            return instruction;
        }
    }
}
