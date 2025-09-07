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
        
        static InstructionHelper()
        {
            // 初始化类型映射表
            InitializeTypeCache();
        }
        
        private static void InitializeTypeCache()
        {
            // 只注册实际存在的类型
            RegisterType(typeof(OpMov<>), 1, typeof(OpMov<byte>));
            RegisterType(typeof(OpMov<>), 2, typeof(OpMov<ushort>));
            RegisterType(typeof(OpMov<>), 4, typeof(OpMov<uint>));
            RegisterType(typeof(OpMov<>), 8, typeof(OpMov<ulong>));
            
            // 检查并注册其他操作类型（如果存在）
            TryRegisterType("OpAdd", typeof(OpAdd<>));
            TryRegisterType("OpSub", typeof(OpSub<>));
            TryRegisterType("OpMul", typeof(OpMul<>));
            TryRegisterType("OpAnd", typeof(OpAnd<>));
            TryRegisterType("OpOr", typeof(OpOr<>));
            TryRegisterType("OpXor", typeof(OpXor<>));
            TryRegisterType("OpNot", typeof(OpNot<>));
            
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
        
        // 尝试注册类型，如果类型存在的话
        private static void TryRegisterType(string typeName, Type genericType)
        {
            try
            {
                // 尝试获取具体类型
                Type byteType = Type.GetType($"Ryujinx.HLE.HOS.Tamper.Operations.{typeName}`1[[System.Byte, System.Private.CoreLib]]");
                Type ushortType = Type.GetType($"Ryujinx.HLE.HOS.Tamper.Operations.{typeName}`1[[System.UInt16, System.Private.CoreLib]]");
                Type uintType = Type.GetType($"Ryujinx.HLE.HOS.Tamper.Operations.{typeName}`1[[System.UInt32, System.Private.CoreLib]]");
                Type ulongType = Type.GetType($"Ryujinx.HLE.HOS.Tamper.Operations.{typeName}`1[[System.UInt64, System.Private.CoreLib]]");
                
                if (byteType != null)
                {
                    RegisterType(genericType, 1, byteType);
                    RegisterType(genericType, 2, ushortType);
                    RegisterType(genericType, 4, uintType);
                    RegisterType(genericType, 8, ulongType);
                }
            }
            catch
            {
                // 类型不存在，忽略
            }
        }
        
        private static void RegisterType(Type genericType, byte width, Type concreteType)
        {
            if (concreteType != null)
            {
                _typeCache[(genericType, width)] = concreteType;
            }
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
            ICondition Create(Type conditionType)
            {
                return (ICondition)InstructionHelper.Create(conditionType, width, lhs, rhs);
            }

            return comparison switch
            {
                Comparison.Greater => Create(typeof(CondGT<>)),
                Comparison.GreaterOrEqual => Create(typeof(CondGE<>)),
                Comparison.Less => Create(typeof(CondLT<>)),
                Comparison.LessOrEqual => Create(typeof(CondLE<>)),
                Comparison.Equal => Create(typeof(CondEQ<>)),
                Comparison.NotEqual => Create(typeof(CondNE<>)),
                _ => throw new TamperCompilationException($"Invalid comparison {comparison} in Atmosphere cheat"),
            };
        }

        public static Object Create(Type instruction, byte width, params Object[] operands)
        {
            if (_typeCache.TryGetValue((instruction, width), out Type realType))
            {
                return Activator.CreateInstance(realType, operands);
            }

            // 如果找不到预注册的类型，回退到原始方法（但会有AOT警告）
            try
            {
                Type fallbackType = width switch
                {
                    1 => instruction.MakeGenericType(typeof(byte)),
                    2 => instruction.MakeGenericType(typeof(ushort)),
                    4 => instruction.MakeGenericType(typeof(uint)),
                    8 => instruction.MakeGenericType(typeof(ulong)),
                    _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
                };
                return Activator.CreateInstance(fallbackType, operands);
            }
            catch (Exception ex)
            {
                throw new TamperCompilationException($"Failed to create instruction {instruction} with width {width}: {ex.Message}");
            }
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
