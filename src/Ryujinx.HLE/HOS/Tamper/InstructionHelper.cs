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
            // 为每个操作类型和宽度注册具体类型
            RegisterType(typeof(OpMov<>), 1, typeof(OpMov<byte>));
            RegisterType(typeof(OpMov<>), 2, typeof(OpMov<ushort>));
            RegisterType(typeof(OpMov<>), 4, typeof(OpMov<uint>));
            RegisterType(typeof(OpMov<>), 8, typeof(OpMov<ulong>));
            
            RegisterType(typeof(OpAdd<>), 1, typeof(OpAdd<byte>));
            RegisterType(typeof(OpAdd<>), 2, typeof(OpAdd<ushort>));
            RegisterType(typeof(OpAdd<>), 4, typeof(OpAdd<uint>));
            RegisterType(typeof(OpAdd<>), 8, typeof(OpAdd<ulong>));
            
            RegisterType(typeof(OpSub<>), 1, typeof(OpSub<byte>));
            RegisterType(typeof(OpSub<>), 2, typeof(OpSub<ushort>));
            RegisterType(typeof(OpSub<>), 4, typeof(OpSub<uint>));
            RegisterType(typeof(OpSub<>), 8, typeof(OpSub<ulong>));
            
            RegisterType(typeof(OpMul<>), 1, typeof(OpMul<byte>));
            RegisterType(typeof(OpMul<>), 2, typeof(OpMul<ushort>));
            RegisterType(typeof(OpMul<>), 4, typeof(OpMul<uint>));
            RegisterType(typeof(OpMul<>), 8, typeof(OpMul<ulong>));
            
            RegisterType(typeof(OpDiv<>), 1, typeof(OpDiv<byte>));
            RegisterType(typeof(OpDiv<>), 2, typeof(OpDiv<ushort>));
            RegisterType(typeof(OpDiv<>), 4, typeof(OpDiv<uint>));
            RegisterType(typeof(OpDiv<>), 8, typeof(OpDiv<ulong>));
            
            RegisterType(typeof(OpAnd<>), 1, typeof(OpAnd<byte>));
            RegisterType(typeof(OpAnd<>), 2, typeof(OpAnd<ushort>));
            RegisterType(typeof(OpAnd<>), 4, typeof(OpAnd<uint>));
            RegisterType(typeof(OpAnd<>), 8, typeof(OpAnd<ulong>));
            
            RegisterType(typeof(OpOr<>), 1, typeof(OpOr<byte>));
            RegisterType(typeof(OpOr<>), 2, typeof(OpOr<ushort>));
            RegisterType(typeof(OpOr<>), 4, typeof(OpOr<uint>));
            RegisterType(typeof(OpOr<>), 8, typeof(OpOr<ulong>));
            
            RegisterType(typeof(OpXor<>), 1, typeof(OpXor<byte>));
            RegisterType(typeof(OpXor<>), 2, typeof(OpXor<ushort>));
            RegisterType(typeof(OpXor<>), 4, typeof(OpXor<uint>));
            RegisterType(typeof(OpXor<>), 8, typeof(OpXor<ulong>));
            
            RegisterType(typeof(OpNot<>), 1, typeof(OpNot<byte>));
            RegisterType(typeof(OpNot<>), 2, typeof(OpNot<ushort>));
            RegisterType(typeof(OpNot<>), 4, typeof(OpNot<uint>));
            RegisterType(typeof(OpNot<>), 8, typeof(OpNot<ulong>));
            
            RegisterType(typeof(OpShiftLeft<>), 1, typeof(OpShiftLeft<byte>));
            RegisterType(typeof(OpShiftLeft<>), 2, typeof(OpShiftLeft<ushort>));
            RegisterType(typeof(OpShiftLeft<>), 4, typeof(OpShiftLeft<uint>));
            RegisterType(typeof(OpShiftLeft<>), 8, typeof(OpShiftLeft<ulong>));
            
            RegisterType(typeof(OpShiftRight<>), 1, typeof(OpShiftRight<byte>));
            RegisterType(typeof(OpShiftRight<>), 2, typeof(OpShiftRight<ushort>));
            RegisterType(typeof(OpShiftRight<>), 4, typeof(OpShiftRight<uint>));
            RegisterType(typeof(OpShiftRight<>), 8, typeof(OpShiftRight<ulong>));
            
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
        
        private static void RegisterType(Type genericType, byte width, Type concreteType)
        {
            _typeCache[(genericType, width)] = concreteType;
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

            throw new TamperCompilationException($"Invalid instruction width {width} or unsupported instruction type {instruction} in Atmosphere cheat");
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
