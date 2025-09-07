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
        
        // 预定义类型映射表，避免使用 MakeGenericType
        private static readonly Dictionary<(Type, byte), Type> _typeCache = new Dictionary<(Type, byte), Type>();
        
        // 预定义类型创建委托
        private static readonly Dictionary<Type, Func<byte, object[], object>> _typeFactories = new Dictionary<Type, Func<byte, object[], object>>();
        
        static InstructionHelper()
        {
            // 初始化类型映射表和工厂方法
            InitializeTypeCache();
            InitializeFactories();
        }
        
        private static void InitializeTypeCache()
        {
            // 只注册已知存在的类型
            RegisterType(typeof(OpMov<>), 1, typeof(OpMov<byte>));
            RegisterType(typeof(OpMov<>), 2, typeof(OpMov<ushort>));
            RegisterType(typeof(OpMov<>), 4, typeof(OpMov<uint>));
            RegisterType(typeof(OpMov<>), 8, typeof(OpMov<ulong>));
            
            // 条件类型
            RegisterType(typeof(CondGT<>), 1, typeof(CondGT<byte>));
            RegisterType(typeof(CondGT<>), 2, typeof(CondGT<ushort>));
            RegisterType(typeof(CondGT<>), 4, typeof(CondGT<uint>));
            RegisterType(typeof(CondGT<>), 8, typeof(CondGT<ulong>));
            
            RegisterType(typeof(CondGE<>), 1, typeof(CondGE<byte>));
            RegisterType(typeof(CondGE<>), 2, typeof(CondGE<ushort>));
            RegisterType(typeof(CondGE<>), 4, typeof(CondGE<uint>));
            RegisterType(typeof(CondGE<>), 8, typeof(CondGE<ulong>));
            
            RegisterType(typeof(CondLT<>), 1, typeof(CondLT<byte>));
            RegisterType(typeof(CondLT<>), 2, typeof(CondLT<ushort>));
            RegisterType(typeof(CondLT<>), 4, typeof(CondLT<uint>));
            RegisterType(typeof(CondLT<>), 8, typeof(CondLT<ulong>));
            
            RegisterType(typeof(CondLE<>), 1, typeof(CondLE<byte>));
            RegisterType(typeof(CondLE<>), 2, typeof(CondLE<ushort>));
            RegisterType(typeof(CondLE<>), 4, typeof(CondLE<uint>));
            RegisterType(typeof(CondLE<>), 8, typeof(CondLE<ulong>));
            
            RegisterType(typeof(CondEQ<>), 1, typeof(CondEQ<byte>));
            RegisterType(typeof(CondEQ<>), 2, typeof(CondEQ<ushort>));
            RegisterType(typeof(CondEQ<>), 4, typeof(CondEQ<uint>));
            RegisterType(typeof(CondEQ<>), 8, typeof(CondEQ<ulong>));
            
            RegisterType(typeof(CondNE<>), 1, typeof(CondNE<byte>));
            RegisterType(typeof(CondNE<>), 2, typeof(CondNE<ushort>));
            RegisterType(typeof(CondNE<>), 4, typeof(CondNE<uint>));
            RegisterType(typeof(CondNE<>), 8, typeof(CondNE<ulong>));
        }
        
        private static void InitializeFactories()
        {
            // 为每个类型注册工厂方法
            RegisterFactory(typeof(OpMov<>), (width, operands) => 
            {
                // 根据 OpMov<T> 的实际构造函数签名调整参数
                // 假设 OpMov<T> 的构造函数接受两个 IOperand 参数
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
        }
        
        private static void RegisterConditionFactory(Type conditionType)
        {
            _typeFactories[conditionType] = (width, operands) =>
            {
                // 根据条件类型的实际构造函数签名调整参数
                // 假设条件类型的构造函数接受两个 IOperand 参数
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
        
        private static void RegisterType(Type genericType, byte width, Type concreteType)
        {
            if (concreteType != null)
            {
                _typeCache[(genericType, width)] = concreteType;
            }
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

            // Instructions are multi-word, with 32bit words. Split the raw instruction
            // and parse each word into individual nybbles of bits.

            var words = rawInstruction.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            byte[] instruction = new byte(WordSize * words.Length);

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
