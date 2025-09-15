using Ryujinx.Common.Logging;
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
            Logger.Debug?.Print(LogClass.TamperMachine, "Initializing instruction factories");
            
            // 为每个类型注册工厂方法
            RegisterFactory(typeof(OpMov<>), (width, operands) => 
            {
                Logger.Debug?.Print(LogClass.TamperMachine, $"Creating OpMov with width {width} and {operands.Length} operands");
                
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
            
            // 注册所有操作类型的工厂方法 - 确保与文件列表匹配
            RegisterOperationFactory(typeof(OpAdd<>));
            RegisterOperationFactory(typeof(OpSub<>));
            RegisterOperationFactory(typeof(OpMul<>));
            RegisterOperationFactory(typeof(OpAnd<>));
            RegisterOperationFactory(typeof(OpOr<>));
            RegisterOperationFactory(typeof(OpXor<>));
            RegisterOperationFactory(typeof(OpNot<>));
            RegisterOperationFactory(typeof(OpLsh<>));
            RegisterOperationFactory(typeof(OpRsh<>));
            
            // 特殊操作的工厂方法（非泛型）
            RegisterFactory(typeof(OpLog<>), (width, operands) =>
            {
                Logger.Debug?.Print(LogClass.TamperMachine, $"Creating OpLog with width {width} and {operands.Length} operands");
                
                if (operands.Length < 2)
                    throw new TamperCompilationException("OpLog requires at least 2 operands");
                
                int logId = (int)operands[0];
                IOperand source = (IOperand)operands[1];
                
                return width switch
                {
                    1 => new OpLog<byte>(logId, source),
                    2 => new OpLog<ushort>(logId, source),
                    4 => new OpLog<uint>(logId, source),
                    8 => new OpLog<ulong>(logId, source),
                    _ => throw new TamperCompilationException($"Invalid instruction width {width} for OpLog"),
                };
            });
            
            // OpProcCtrl 是非泛型操作，单独处理
            _typeFactories[typeof(OpProcCtrl)] = (width, operands) =>
            {
                Logger.Debug?.Print(LogClass.TamperMachine, $"Creating OpProcCtrl with {operands.Length} operands");
                
                if (operands.Length < 2)
                    throw new TamperCompilationException("OpProcCtrl requires at least 2 operands");
                
                ITamperedProcess process = (ITamperedProcess)operands[0];
                bool pause = (bool)operands[1];
                
                return new OpProcCtrl(process, pause);
            };
            
            Logger.Debug?.Print(LogClass.TamperMachine, "Instruction factories initialized successfully");
        }
        
        private static void RegisterConditionFactory(Type conditionType)
        {
            _typeFactories[conditionType] = (width, operands) =>
            {
                Logger.Debug?.Print(LogClass.TamperMachine, $"Creating {conditionType.Name} with width {width} and {operands.Length} operands");
                
                if (operands.Length < 2)
                    throw new TamperCompilationException("Condition requires at least 2 operands");
                
                IOperand lhs = (IOperand)operands[0];
                IOperand rhs = (IOperand)operands[1];
                
                return width switch
                {
                    1 => conditionType == typeof(CondGT<>) ? (object)new CondGT<byte>(lhs, rhs) :
                          conditionType == typeof(CondGE<>) ? (object)new CondGE<byte>(lhs, rhs) :
                          conditionType == typeof(CondLT<>) ? (object)new CondLT<byte>(lhs, rhs) :
                          conditionType == typeof(CondLE<>) ? (object)new CondLE<byte>(lhs, rhs) :
                          conditionType == typeof(CondEQ<>) ? (object)new CondEQ<byte>(lhs, rhs) :
                          conditionType == typeof(CondNE<>) ? (object)new CondNE<byte>(lhs, rhs) :
                          throw new TamperCompilationException($"Unsupported condition type {conditionType}"),
                    2 => conditionType == typeof(CondGT<>) ? (object)new CondGT<ushort>(lhs, rhs) :
                          conditionType == typeof(CondGE<>) ? (object)new CondGE<ushort>(lhs, rhs) :
                          conditionType == typeof(CondLT<>) ? (object)new CondLT<ushort>(lhs, rhs) :
                          conditionType == typeof(CondLE<>) ? (object)new CondLE<ushort>(lhs, rhs) :
                          conditionType == typeof(CondEQ<>) ? (object)new CondEQ<ushort>(lhs, rhs) :
                          conditionType == typeof(CondNE<>) ? (object)new CondNE<ushort>(lhs, rhs) :
                          throw new TamperCompilationException($"Unsupported condition type {conditionType}"),
                    4 => conditionType == typeof(CondGT<>) ? (object)new CondGT<uint>(lhs, rhs) :
                          conditionType == typeof(CondGE<>) ? (object)new CondGE<uint>(lhs, rhs) :
                          conditionType == typeof(CondLT<>) ? (object)new CondLT<uint>(lhs, rhs) :
                          conditionType == typeof(CondLE<>) ? (object)new CondLE<uint>(lhs, rhs) :
                          conditionType == typeof(CondEQ<>) ? (object)new CondEQ<uint>(lhs, rhs) :
                          conditionType == typeof(CondNE<>) ? (object)new CondNE<uint>(lhs, rhs) :
                          throw new TamperCompilationException($"Unsupported condition type {conditionType}"),
                    8 => conditionType == typeof(CondGT<>) ? (object)new CondGT<ulong>(lhs, rhs) :
                          conditionType == typeof(CondGE<>) ? (object)new CondGE<ulong>(lhs, rhs) :
                          conditionType == typeof(CondLT<>) ? (object)new CondLT<ulong>(lhs, rhs) :
                          conditionType == typeof(CondLE<>) ? (object)new CondLE<ulong>(lhs, rhs) :
                          conditionType == typeof(CondEQ<>) ? (object)new CondEQ<ulong>(lhs, rhs) :
                          conditionType == typeof(CondNE<>) ? (object)new CondNE<ulong>(lhs, rhs) :
                          throw new TamperCompilationException($"Unsupported condition type {conditionType}"),
                    _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
                };
            };
        }
        
        private static void RegisterOperationFactory(Type operationType)
        {
            _typeFactories[operationType] = (width, operands) =>
            {
                Logger.Debug?.Print(LogClass.TamperMachine, $"Creating {operationType.Name} with width {width} and {operands.Length} operands");
                
                try
                {
                    return width switch
                    {
                        1 => operationType == typeof(OpAdd<>) ? (object)new OpAdd<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpSub<>) ? (object)new OpSub<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpMul<>) ? (object)new OpMul<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpAnd<>) ? (object)new OpAnd<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpOr<>) ? (object)new OpOr<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpXor<>) ? (object)new OpXor<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpNot<>) ? (object)new OpNot<byte>((IOperand)operands[0], (IOperand)operands[1]) :
                              operationType == typeof(OpLsh<>) ? (object)new OpLsh<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpRsh<>) ? (object)new OpRsh<byte>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              throw new TamperCompilationException($"Unsupported operation type {operationType}"),
                        2 => operationType == typeof(OpAdd<>) ? (object)new OpAdd<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpSub<>) ? (object)new OpSub<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpMul<>) ? (object)new OpMul<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpAnd<>) ? (object)new OpAnd<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpOr<>) ? (object)new OpOr<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpXor<>) ? (object)new OpXor<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpNot<>) ? (object)new OpNot<ushort>((IOperand)operands[0], (IOperand)operands[1]) :
                              operationType == typeof(OpLsh<>) ? (object)new OpLsh<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpRsh<>) ? (object)new OpRsh<ushort>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              throw new TamperCompilationException($"Unsupported operation type {operationType}"),
                        4 => operationType == typeof(OpAdd<>) ? (object)new OpAdd<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpSub<>) ? (object)new OpSub<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpMul<>) ? (object)new OpMul<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpAnd<>) ? (object)new OpAnd<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpOr<>) ? (object)new OpOr<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpXor<>) ? (object)new OpXor<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpNot<>) ? (object)new OpNot<uint>((IOperand)operands[0], (IOperand)operands[1]) :
                              operationType == typeof(OpLsh<>) ? (object)new OpLsh<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpRsh<>) ? (object)new OpRsh<uint>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              throw new TamperCompilationException($"Unsupported operation type {operationType}"),
                        8 => operationType == typeof(OpAdd<>) ? (object)new OpAdd<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpSub<>) ? (object)new OpSub<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpMul<>) ? (object)new OpMul<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpAnd<>) ? (object)new OpAnd<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpOr<>) ? (object)new OpOr<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpXor<>) ? (object)new OpXor<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpNot<>) ? (object)new OpNot<ulong>((IOperand)operands[0], (IOperand)operands[1]) :
                              operationType == typeof(OpLsh<>) ? (object)new OpLsh<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              operationType == typeof(OpRsh<>) ? (object)new OpRsh<ulong>((IOperand)operands[0], (IOperand)operands[1], (IOperand)operands[2]) :
                              throw new TamperCompilationException($"Unsupported operation type {operationType}"),
                        _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
                    };
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.TamperMachine, $"Failed to create {operationType.Name} with width {width}: {ex.Message}");
                    throw;
                }
            };
        }
        
        private static void RegisterFactory(Type genericType, Func<byte, object[], object> factory)
        {
            _typeFactories[genericType] = factory;
            Logger.Debug?.Print(LogClass.TamperMachine, $"Registered factory for {genericType.Name}");
        }

        public static void Emit(IOperation operation, CompilationContext context)
        {
            context.CurrentOperations.Add(operation);
        }

        public static void Emit(Type instruction, byte width, CompilationContext context, params Object[] operands)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, $"Emitting instruction: {instruction.Name} with width {width}");
            
            try
            {
                IOperation operation = (IOperation)Create(instruction, width, operands);
                Emit(operation, context);
                Logger.Debug?.Print(LogClass.TamperMachine, $"Successfully emitted instruction: {instruction.Name}");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, $"Failed to emit instruction {instruction.Name}: {ex.Message}");
                throw;
            }
        }

        public static void EmitMov(byte width, CompilationContext context, IOperand destination, IOperand source)
        {
            Emit(typeof(OpMov<>), width, context, destination, source);
        }

        public static ICondition CreateCondition(Comparison comparison, byte width, IOperand lhs, IOperand rhs)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, $"Creating condition: {comparison} with width {width}");
            
            try
            {
                ICondition condition = comparison switch
                {
                    Comparison.Greater => (ICondition)Create(typeof(CondGT<>), width, lhs, rhs),
                    Comparison.GreaterOrEqual => (ICondition)Create(typeof(CondGE<>), width, lhs, rhs),
                    Comparison.Less => (ICondition)Create(typeof(CondLT<>), width, lhs, rhs),
                    Comparison.LessOrEqual => (ICondition)Create(typeof(CondLE<>), width, lhs, rhs),
                    Comparison.Equal => (ICondition)Create(typeof(CondEQ<>), width, lhs, rhs),
                    Comparison.NotEqual => (ICondition)Create(typeof(CondNE<>), width, lhs, rhs),
                    _ => throw new TamperCompilationException($"Invalid comparison {comparison} in Atmosphere cheat"),
                };
                
                Logger.Debug?.Print(LogClass.TamperMachine, $"Successfully created condition: {comparison}");
                return condition;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, $"Failed to create condition {comparison}: {ex.Message}");
                throw;
            }
        }

        public static Object Create(Type instruction, byte width, params Object[] operands)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, $"Creating instruction: {instruction.Name} with width {width} and {operands.Length} operands");
            
            if (_typeFactories.TryGetValue(instruction, out var factory))
            {
                try
                {
                    var result = factory(width, operands);
                    Logger.Debug?.Print(LogClass.TamperMachine, $"Successfully created instruction: {instruction.Name}");
                    return result;
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.TamperMachine, $"Factory failed to create instruction {instruction.Name} with width {width}: {ex.Message}");
                    throw new TamperCompilationException($"Failed to create instruction {instruction.Name} with width {width}: {ex.Message}");
                }
            }

            Logger.Error?.Print(LogClass.TamperMachine, $"Unsupported instruction type {instruction.Name} with width {width}");
            throw new TamperCompilationException($"Unsupported instruction type {instruction.Name} with width {width} in Atmosphere cheat");
        }

        // 回滚到版本15的GetImmediate实现
        public static ulong GetImmediate(byte[] instruction, int index, int nybbleCount)
        {
            ulong value = 0;

            for (int i = 0; i < nybbleCount; i++)
            {
                value <<= 4;
                value |= instruction[index + i];
            }

            Logger.Debug?.Print(LogClass.TamperMachine, $"Extracted immediate value: 0x{value:X} from position {index} with {nybbleCount} nybbles");

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

            Logger.Debug?.Print(LogClass.TamperMachine, $"Detected code type: {(CodeType)codeType} (0x{codeType:X})");

            return (CodeType)codeType;
        }

        // 回滚到版本15的ParseRawInstruction实现
        public static byte[] ParseRawInstruction(string rawInstruction)
        {
            const int WordSize = 2 * sizeof(uint); // 每个32位字由8个十六进制字符表示

            Logger.Debug?.Print(LogClass.TamperMachine, $"Parsing raw instruction: {rawInstruction}");

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

            Logger.Debug?.Print(LogClass.TamperMachine, $"Parsed instruction: {BitConverter.ToString(instruction).Replace("-", " ")}");

            return instruction;
        }
    }
}
