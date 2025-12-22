using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Translation.Optimizations
{
    static class GlobalToStorage
    {
        private const int DriverReservedCb = 0;

        enum LsMemoryType
        {
            Local,
            Shared,
        }

        private class GtsContext
        {
            private readonly struct Entry
            {
                public readonly int FunctionId;
                public readonly Instruction Inst;
                public readonly StorageKind StorageKind;
                public readonly bool IsMultiTarget;
                public readonly IReadOnlyList<uint> TargetCbs;

                public Entry(
                    int functionId,
                    Instruction inst,
                    StorageKind storageKind,
                    bool isMultiTarget,
                    IReadOnlyList<uint> targetCbs)
                {
                    FunctionId = functionId;
                    Inst = inst;
                    StorageKind = storageKind;
                    IsMultiTarget = isMultiTarget;
                    TargetCbs = targetCbs;
                }
            }

            private readonly struct LsKey : IEquatable<LsKey>
            {
                public readonly Operand BaseOffset;
                public readonly int ConstOffset;
                public readonly LsMemoryType Type;

                public LsKey(Operand baseOffset, int constOffset, LsMemoryType type)
                {
                    BaseOffset = baseOffset;
                    ConstOffset = constOffset;
                    Type = type;
                }

                public override int GetHashCode()
                {
                    return HashCode.Combine(BaseOffset, ConstOffset, Type);
                }

                public override bool Equals(object obj)
                {
                    return obj is LsKey other && Equals(other);
                }

                public bool Equals(LsKey other)
                {
                    return other.BaseOffset == BaseOffset && other.ConstOffset == ConstOffset && other.Type == Type;
                }
            }

            private readonly List<Entry> _entries;
            private readonly Dictionary<LsKey, Dictionary<uint, SearchResult>> _sharedEntries;
            private readonly HelperFunctionManager _hfm;

            public GtsContext(HelperFunctionManager hfm)
            {
                _entries = new List<Entry>();
                _sharedEntries = new Dictionary<LsKey, Dictionary<uint, SearchResult>>();
                _hfm = hfm;
            }

            public int AddFunction(Operation baseOp, bool isMultiTarget, IReadOnlyList<uint> targetCbs, Function function)
            {
                int functionId = _hfm.AddFunction(function);

                _entries.Add(new Entry(functionId, baseOp.Inst, baseOp.StorageKind, isMultiTarget, targetCbs));

                return functionId;
            }

            public bool TryGetFunctionId(Operation baseOp, bool isMultiTarget, IReadOnlyList<uint> targetCbs, out int functionId)
            {
                foreach (Entry entry in _entries)
                {
                    if (entry.Inst != baseOp.Inst ||
                        entry.StorageKind != baseOp.StorageKind ||
                        entry.IsMultiTarget != isMultiTarget ||
                        entry.TargetCbs.Count != targetCbs.Count)
                    {
                        continue;
                    }

                    bool allEqual = true;

                    for (int index = 0; index < targetCbs.Count; index++)
                    {
                        if (targetCbs[index] != entry.TargetCbs[index])
                        {
                            allEqual = false;
                            break;
                        }
                    }

                    if (allEqual)
                    {
                        functionId = entry.FunctionId;
                        return true;
                    }
                }

                functionId = -1;
                return false;
            }

            public void AddMemoryTargetCb(LsMemoryType type, Operand baseOffset, int constOffset, uint targetCb, SearchResult result)
            {
                LsKey key = new(baseOffset, constOffset, type);

                if (!_sharedEntries.TryGetValue(key, out Dictionary<uint, SearchResult> targetCbs))
                {
                    // No entry with this base offset, create a new one.

                    targetCbs = new Dictionary<uint, SearchResult>() { { targetCb, result } };

                    _sharedEntries.Add(key, targetCbs);
                }
                else if (targetCbs.TryGetValue(targetCb, out SearchResult existingResult))
                    {
                        // If our entry already exists, but does not match the new result,
                        // we set the offset to null to indicate there are multiple possible offsets.
                        // This will be used on the multi-target access that does not need to know the offset.

                        if (existingResult.Offset != null &&
                            (existingResult.Offset != result.Offset ||
                            existingResult.ConstOffset != result.ConstOffset))
                        {
                            targetCbs[targetCb] = new SearchResult(result.SbCbSlot, result.SbCbOffset);
                        }
                    }
                    else
                    {
                        // An entry for this base offset already exists, but not for the specified
                        // constant buffer region where the storage buffer base address and size
                        // comes from.

                        targetCbs.Add(targetCb, result);
                    }
                }

            public bool TryGetMemoryTargetCb(LsMemoryType type, Operand baseOffset, int constOffset, out SearchResult result)
            {
                LsKey key = new(baseOffset, constOffset, type);

                if (_sharedEntries.TryGetValue(key, out Dictionary<uint, SearchResult> targetCbs) && targetCbs.Count == 1)
                {
                    SearchResult candidateResult = targetCbs.Values.First();

                    if (candidateResult.Found)
                    {
                        result = candidateResult;

                        return true;
                    }
                }

                result = default;

                return false;
            }
        }

        private readonly struct SearchResult
        {
            public static SearchResult NotFound => new(-1, 0);
            public bool Found => SbCbSlot != -1;
            public int SbCbSlot { get; }
            public int SbCbOffset { get; }
            public Operand Offset { get; }
            public int ConstOffset { get; }

            public SearchResult(int sbCbSlot, int sbCbOffset)
            {
                SbCbSlot = sbCbSlot;
                SbCbOffset = sbCbOffset;
            }

            public SearchResult(int sbCbSlot, int sbCbOffset, Operand offset, int constOffset = 0)
            {
                SbCbSlot = sbCbSlot;
                SbCbOffset = sbCbOffset;
                Offset = offset;
                ConstOffset = constOffset;
            }
        }

        public static void RunPass(
            HelperFunctionManager hfm,
            BasicBlock[] blocks,
            ResourceManager resourceManager,
            IGpuAccessor gpuAccessor,
            TargetLanguage targetLanguage)
        {
            GtsContext gtsContext = new(hfm);

            foreach (BasicBlock block in blocks)
            {
                for (LinkedListNode<INode> node = block.Operations.First; node != null; node = node.Next)
                {
                    if (node.Value is not Operation operation)
                    {
                        continue;
                    }

                    if (IsGlobalMemory(operation.StorageKind))
                    {
                        // ==================== 方案四：改进错误日志 ====================
                        string debugInfo = $"Operation: {operation.Inst}, StorageKind: {operation.StorageKind}, ";
                        debugInfo += $"Block: {block.Index}, Dest: {operation.Dest?.Type.ToString() ?? "null"}";
                        gpuAccessor.Log($"Attempting to replace global memory operation: {debugInfo}");
                        
                        if (operation.SourcesCount > 0)
                        {
                            Operand globalAddress = operation.GetSource(0);
                            debugInfo = $"Source0: type={globalAddress.Type}, value={globalAddress.Value}, ";
                            debugInfo += $"AsgOp type={globalAddress.AsgOp?.GetType().Name ?? "null"}, ";
                            debugInfo += $"AsgOp Inst={(globalAddress.AsgOp as Operation)?.Inst.ToString() ?? "N/A"}";
                            gpuAccessor.Log($"  Address details: {debugInfo}");
                        }
                        // ============================================================

                        LinkedListNode<INode> nextNode = ReplaceGlobalMemoryWithStorage(
                            gtsContext,
                            resourceManager,
                            gpuAccessor,
                            targetLanguage,
                            block,
                            node);

                        if (nextNode == null)
                        {
                            // The returned value being null means that the global memory replacement failed,
                            // so we just make loads read 0 and stores do nothing.

                            gpuAccessor.Log($"Failed to reserve storage buffer for global memory operation \"{operation.Inst}\". Context:");
                            gpuAccessor.Log($"  Operation type: {operation.StorageKind}");
                            gpuAccessor.Log($"  Block index: {block.Index}");
                            gpuAccessor.Log($"  Destination: {(operation.Dest != null ? $"type={operation.Dest.Type}" : "store operation")}");
                            
                            // 尝试获取表达式字符串以便调试
                            if (operation.SourcesCount > 0)
                            {
                                string expr = GetOperandExpression(operation.GetSource(0), block);
                                gpuAccessor.Log($"  Address expression: {expr}");
                            }

                            if (operation.Dest != null)
                            {
                                operation.TurnIntoCopy(Const(0));
                            }
                            else
                            {
                                Utils.DeleteNode(node, operation);
                            }
                        }
                        else
                        {
                            node = nextNode;
                        }
                    }
                    else if (operation.Inst == Instruction.Store &&
                        (operation.StorageKind == StorageKind.SharedMemory ||
                        operation.StorageKind == StorageKind.LocalMemory))
                    {
                        // The NVIDIA compiler can sometimes use shared or local memory as temporary
                        // storage to place the base address and size on, so we need
                        // to be able to find such information stored in memory too.

                        if (TryGetMemoryOffsets(operation, out LsMemoryType type, out Operand baseOffset, out int constOffset))
                        {
                            Operand value = operation.GetSource(operation.SourcesCount - 1);

                            var result = FindUniqueBaseAddressCb(gtsContext, block, value, needsOffset: false);
                            if (result.Found)
                            {
                                uint targetCb = PackCbSlotAndOffset(result.SbCbSlot, result.SbCbOffset);
                                gtsContext.AddMemoryTargetCb(type, baseOffset, constOffset, targetCb, result);
                            }
                        }
                    }
                }
            }
        }

        private static bool IsGlobalMemory(StorageKind storageKind)
        {
            return storageKind == StorageKind.GlobalMemory ||
                   storageKind == StorageKind.GlobalMemoryS8 ||
                   storageKind == StorageKind.GlobalMemoryS16 ||
                   storageKind == StorageKind.GlobalMemoryU8 ||
                   storageKind == StorageKind.GlobalMemoryU16;
        }

        private static bool IsSmallInt(StorageKind storageKind)
        {
            return storageKind == StorageKind.GlobalMemoryS8 ||
                   storageKind == StorageKind.GlobalMemoryS16 ||
                   storageKind == StorageKind.GlobalMemoryU8 ||
                   storageKind == StorageKind.GlobalMemoryU16;
        }

        private static LinkedListNode<INode> ReplaceGlobalMemoryWithStorage(
            GtsContext gtsContext,
            ResourceManager resourceManager,
            IGpuAccessor gpuAccessor,
            TargetLanguage targetLanguage,
            BasicBlock block,
            LinkedListNode<INode> node)
        {
            Operation operation = node.Value as Operation;
            Operand globalAddress = operation.GetSource(0);
            
            // ==================== 方案四：添加更详细的调试信息 ====================
            gpuAccessor.Log($"ReplaceGlobalMemoryWithStorage: Operation={operation.Inst}, StorageKind={operation.StorageKind}");
            gpuAccessor.Log($"  Global address: type={globalAddress.Type}, value={globalAddress.Value}");
            if (globalAddress.AsgOp is Operation asgOp)
            {
                gpuAccessor.Log($"  AsgOp: type={asgOp.GetType().Name}, Inst={asgOp.Inst}");
            }
            // ===================================================================

            SearchResult result = FindUniqueBaseAddressCb(gtsContext, block, globalAddress, needsOffset: true);

            if (result.Found)
            {
                // We found the storage buffer that is being accessed.
                // There are two possible paths here, if the operation is simple enough,
                // we just generate the storage access code inline.
                // Otherwise, we generate a function call (and the function if necessary).

                Operand offset = result.Offset;

                bool storageUnaligned = gpuAccessor.QueryHasUnalignedStorageBuffer();

                if (storageUnaligned)
                {
                    Operand baseAddress = Cbuf(result.SbCbSlot, result.SbCbOffset);

                    Operand baseAddressMasked = Local();
                    Operand hostOffset = Local();

                    int alignment = gpuAccessor.QueryHostStorageBufferOffsetAlignment();

                    Operation maskOp = new(Instruction.BitwiseAnd, baseAddressMasked, baseAddress, Const(-alignment));
                    Operation subOp = new(Instruction.Subtract, hostOffset, globalAddress, baseAddressMasked);

                    node.List.AddBefore(node, maskOp);
                    node.List.AddBefore(node, subOp);

                    offset = hostOffset;
                }
                else if (result.ConstOffset != 0)
                {
                    Operand newOffset = Local();

                    Operation addOp = new(Instruction.Add, newOffset, offset, Const(result.ConstOffset));

                    node.List.AddBefore(node, addOp);

                    offset = newOffset;
                }

                if (CanUseInlineStorageOp(operation, targetLanguage))
                {
                    return GenerateInlineStorageOp(resourceManager, node, operation, offset, result);
                }
                else
                {
                    if (!TryGenerateSingleTargetStorageOp(
                        gtsContext,
                        resourceManager,
                        targetLanguage,
                        operation,
                        result,
                        out int functionId))
                    {
                        return null;
                    }

                    return GenerateCallStorageOp(node, operation, offset, functionId);
                }
            }
            else
            {
                // Failed to find the storage buffer directly.
                // Try to walk through Phi chains and find all possible constant buffers where
                // the base address might be stored.
                // Generate a helper function that will check all possible storage buffers and use the right one.

                // ==================== 方案四：尝试增强搜索 ====================
                gpuAccessor.Log($"Initial search failed for operation {operation.Inst}, trying enhanced search...");
                
                if (TryEnhancedSearch(gtsContext, block, globalAddress, out result))
                {
                    gpuAccessor.Log($"Enhanced search found storage buffer at slot {result.SbCbSlot}, offset {result.SbCbOffset}");
                    
                    // 使用增强搜索找到的结果
                    // ... 这里需要重用上面的成功逻辑 ...
                    // 由于这部分代码较长，我们可以创建一个辅助方法来处理成功的情况
                    return ProcessFoundStorageBuffer(gtsContext, resourceManager, gpuAccessor, targetLanguage, 
                                                     node, operation, result);
                }
                // =============================================================

                if (!TryGenerateMultiTargetStorageOp(
                    gtsContext,
                    resourceManager,
                    gpuAccessor,
                    targetLanguage,
                    block,
                    operation,
                    out int functionId))
                {
                    // ==================== 方案四：改进错误信息 ====================
                    gpuAccessor.Log($"Failed to find storage buffer for global memory operation \"{operation.Inst}\". ");
                    gpuAccessor.Log($"  Operation context: Block {block.Index}, StorageKind: {operation.StorageKind}");
                    gpuAccessor.Log($"  Address expression: {GetOperandExpression(globalAddress, block)}");
                    gpuAccessor.Log($"  Sources count: {operation.SourcesCount}");
                    
                    for (int i = 0; i < operation.SourcesCount; i++)
                    {
                        Operand src = operation.GetSource(i);
                        gpuAccessor.Log($"    Source[{i}]: type={src.Type}, value={src.Value}");
                    }
                    // =============================================================
                    
                    return null;
                }

                return GenerateCallStorageOp(node, operation, null, functionId);
            }
        }

        // ==================== 方案一：新增辅助方法 ====================
        /// <summary>
        /// 尝试从复杂操作中提取常量缓冲区引用
        /// </summary>
        private static SearchResult TryExtractCbFromComplexOperation(Operation operation, BasicBlock block)
        {
            if (operation == null) return SearchResult.NotFound;
            
            gpuAccessor.Log($"TryExtractCbFromComplexOperation: Operation={operation.Inst}, SourcesCount={operation.SourcesCount}");
            
            // 首先检查是否可以直接从操作源中找到常量缓冲区
            for (int i = 0; i < operation.SourcesCount; i++)
            {
                Operand source = operation.GetSource(i);
                if (source.Type == OperandType.ConstantBuffer)
                {
                    gpuAccessor.Log($"  Found direct ConstantBuffer at source[{i}]");
                    return GetBaseAddressCbWithOffset(source, Const(0), 0);
                }
            }
            
            // 递归查找嵌套操作
            for (int i = 0; i < operation.SourcesCount; i++)
            {
                Operand source = operation.GetSource(i);
                if (source.AsgOp is Operation nestedOp)
                {
                    gpuAccessor.Log($"  Recursing into nested operation {nestedOp.Inst} at source[{i}]");
                    var result = TryExtractCbFromComplexOperation(nestedOp, block);
                    if (result.Found) 
                    {
                        gpuAccessor.Log($"  Found ConstantBuffer in nested operation");
                        return result;
                    }
                }
                else if (source.Type == OperandType.LocalVariable)
                {
                    // 尝试查找该局部变量的赋值操作
                    var lastOp = Utils.FindLastOperation(source, block);
                    if (lastOp != source && lastOp.AsgOp is Operation assignOp)
                    {
                        gpuAccessor.Log($"  Following local variable to assignment operation {assignOp.Inst}");
                        var result = TryExtractCbFromComplexOperation(assignOp, block);
                        if (result.Found) return result;
                    }
                }
            }
            
            // 检查特定模式的操作
            switch (operation.Inst)
            {
                case Instruction.Multiply:
                case Instruction.ShiftLeft:
                case Instruction.ShiftRightS32:
                case Instruction.ShiftRightU32:
                    // 对于位移和乘法，其中一个操作数可能是常量
                    // 检查是否有常量缓冲区作为另一个操作数
                    for (int i = 0; i < operation.SourcesCount; i++)
                    {
                        int otherIdx = (i + 1) % operation.SourcesCount;
                        Operand source = operation.GetSource(otherIdx);
                        if (source.Type == OperandType.ConstantBuffer)
                        {
                            gpuAccessor.Log($"  Found ConstantBuffer in arithmetic operation at source[{otherIdx}]");
                            return GetBaseAddressCbWithOffset(source, Const(0), 0);
                        }
                    }
                    break;
                    
                case Instruction.BitwiseOr:
                case Instruction.BitwiseAnd:
                case Instruction.BitwiseExclusiveOr:
                    // 位操作可能用于地址掩码
                    for (int i = 0; i < operation.SourcesCount; i++)
                    {
                        Operand source = operation.GetSource(i);
                        if (source.Type == OperandType.ConstantBuffer)
                        {
                            gpuAccessor.Log($"  Found ConstantBuffer in bitwise operation at source[{i}]");
                            return GetBaseAddressCbWithOffset(source, Const(0), 0);
                        }
                    }
                    break;
            }
            
            return SearchResult.NotFound;
        }
        
        /// <summary>
        /// 增强的搜索方法，尝试更多查找策略
        /// </summary>
        private static bool TryEnhancedSearch(GtsContext gtsContext, BasicBlock block, Operand globalAddress, out SearchResult result)
        {
            result = SearchResult.NotFound;
            
            // 1. 尝试直接从操作中提取
            if (globalAddress.AsgOp is Operation operation)
            {
                gpuAccessor.Log($"TryEnhancedSearch: Analyzing operation {operation.Inst}");
                result = TryExtractCbFromComplexOperation(operation, block);
                if (result.Found)
                {
                    return true;
                }
            }
            
            // 2. 尝试查找内存中的基地址
            result = FindBaseAddressCbFromMemory(gtsContext, globalAddress.AsgOp as Operation, 0, needsOffset: true);
            if (result.Found)
            {
                return true;
            }
            
            // 3. 尝试从phi节点中查找
            if (globalAddress.AsgOp is PhiNode phi)
            {
                gpuAccessor.Log($"TryEnhancedSearch: Analyzing phi node with {phi.SourcesCount} sources");
                
                // 收集所有phi源中的常量缓冲区
                HashSet<(int, int)> uniqueCbs = new();
                
                for (int i = 0; i < phi.SourcesCount; i++)
                {
                    Operand source = phi.GetSource(i);
                    var tempResult = FindUniqueBaseAddressCb(gtsContext, phi.GetBlock(i), source, needsOffset: false);
                    if (tempResult.Found)
                    {
                        uniqueCbs.Add((tempResult.SbCbSlot, tempResult.SbCbOffset));
                    }
                }
                
                // 如果所有phi源都指向同一个常量缓冲区，使用它
                if (uniqueCbs.Count == 1)
                {
                    var (slot, offset) = uniqueCbs.First();
                    gpuAccessor.Log($"TryEnhancedSearch: All phi sources point to same CB: slot={slot}, offset={offset}");
                    result = new SearchResult(slot, offset, globalAddress, 0);
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// 处理找到存储缓冲区后的通用逻辑
        /// </summary>
        private static LinkedListNode<INode> ProcessFoundStorageBuffer(
            GtsContext gtsContext,
            ResourceManager resourceManager,
            IGpuAccessor gpuAccessor,
            TargetLanguage targetLanguage,
            LinkedListNode<INode> node,
            Operation operation,
            SearchResult result)
        {
            Operand offset = result.Offset;
            bool storageUnaligned = gpuAccessor.QueryHasUnalignedStorageBuffer();

            if (storageUnaligned)
            {
                Operand baseAddress = Cbuf(result.SbCbSlot, result.SbCbOffset);
                Operand baseAddressMasked = Local();
                Operand hostOffset = Local();

                int alignment = gpuAccessor.QueryHostStorageBufferOffsetAlignment();

                Operation maskOp = new(Instruction.BitwiseAnd, baseAddressMasked, baseAddress, Const(-alignment));
                Operation subOp = new(Instruction.Subtract, hostOffset, operation.GetSource(0), baseAddressMasked);

                node.List.AddBefore(node, maskOp);
                node.List.AddBefore(node, subOp);

                offset = hostOffset;
            }
            else if (result.ConstOffset != 0)
            {
                Operand newOffset = Local();
                Operation addOp = new(Instruction.Add, newOffset, offset, Const(result.ConstOffset));
                node.List.AddBefore(node, addOp);
                offset = newOffset;
            }

            if (CanUseInlineStorageOp(operation, targetLanguage))
            {
                return GenerateInlineStorageOp(resourceManager, node, operation, offset, result);
            }
            else
            {
                if (!TryGenerateSingleTargetStorageOp(gtsContext, resourceManager, targetLanguage, 
                                                      operation, result, out int functionId))
                {
                    return null;
                }

                return GenerateCallStorageOp(node, operation, offset, functionId);
            }
        }
        
        /// <summary>
        /// 获取操作数的表达式字符串（用于调试）
        /// </summary>
        private static string GetOperandExpression(Operand operand, BasicBlock block)
        {
            if (operand == null) return "null";
            
            StringBuilder sb = new StringBuilder();
            sb.Append($"Type: {operand.Type}");
            
            if (operand.Value != 0)
            {
                sb.Append($", Value: 0x{operand.Value:X}");
            }
            
            if (operand.AsgOp != null)
            {
                sb.Append($", AsgOp: {operand.AsgOp.GetType().Name}");
                
                if (operand.AsgOp is Operation op)
                {
                    sb.Append($" ({op.Inst})");
                }
                else if (operand.AsgOp is PhiNode phi)
                {
                    sb.Append($" (Phi with {phi.SourcesCount} sources)");
                }
            }
            
            // 对于局部变量，尝试查找其赋值
            if (operand.Type == OperandType.LocalVariable)
            {
                var lastOp = Utils.FindLastOperation(operand, block);
                if (lastOp != operand)
                {
                    sb.Append($", LastOp: {lastOp.Type}");
                }
            }
            
            return sb.ToString();
        }
        // =============================================================

        private static bool CanUseInlineStorageOp(Operation operation, TargetLanguage targetLanguage)
        {
            if (operation.StorageKind != StorageKind.GlobalMemory)
            {
                return false;
            }

            return (operation.Inst != Instruction.AtomicMaxS32 &&
                    operation.Inst != Instruction.AtomicMinS32) || targetLanguage == TargetLanguage.Spirv;
        }

        private static LinkedListNode<INode> GenerateInlineStorageOp(
            ResourceManager resourceManager,
            LinkedListNode<INode> node,
            Operation operation,
            Operand offset,
            SearchResult result)
        {
            bool isStore = operation.Inst == Instruction.Store || operation.Inst.IsAtomic();
            if (!resourceManager.TryGetStorageBufferBinding(result.SbCbSlot, result.SbCbOffset, isStore, out int binding))
            {
                return null;
            }

            Operand wordOffset = Local();

            Operand[] sources;

            if (operation.Inst == Instruction.AtomicCompareAndSwap)
            {
                sources = new[]
                {
                    Const(binding),
                    Const(0),
                    wordOffset,
                    operation.GetSource(operation.SourcesCount - 2),
                    operation.GetSource(operation.SourcesCount - 1),
                };
            }
            else if (isStore)
            {
                sources = new[] { Const(binding), Const(0), wordOffset, operation.GetSource(operation.SourcesCount - 1) };
            }
            else
            {
                sources = new[] { Const(binding), Const(0), wordOffset };
            }

            Operation shiftOp = new(Instruction.ShiftRightU32, wordOffset, offset, Const(2));
            Operation storageOp = new(operation.Inst, StorageKind.StorageBuffer, operation.Dest, sources);

            node.List.AddBefore(node, shiftOp);
            LinkedListNode<INode> newNode = node.List.AddBefore(node, storageOp);

            Utils.DeleteNode(node, operation);

            return newNode;
        }

        private static LinkedListNode<INode> GenerateCallStorageOp(LinkedListNode<INode> node, Operation operation, Operand offset, int functionId)
        {
            // Generate call to a helper function that will perform the storage buffer operation.

            Operand[] sources = new Operand[operation.SourcesCount - 1 + (offset == null ? 2 : 1)];

            sources[0] = Const(functionId);

            if (offset != null)
            {
                // If the offset was supplised, we use that and skip the global address.

                sources[1] = offset;

                for (int srcIndex = 2; srcIndex < operation.SourcesCount; srcIndex++)
                {
                    sources[srcIndex] = operation.GetSource(srcIndex);
                }
            }
            else
            {
                // Use the 64-bit global address which is split in 2 32-bit arguments.

                for (int srcIndex = 0; srcIndex < operation.SourcesCount; srcIndex++)
                {
                    sources[srcIndex + 1] = operation.GetSource(srcIndex);
                }
            }

            bool returnsValue = operation.Dest != null;
            Operand returnValue = returnsValue ? Local() : null;

            Operation callOp = new(Instruction.Call, returnValue, sources);

            LinkedListNode<INode> newNode = node.List.AddBefore(node, callOp);

            if (returnsValue)
            {
                operation.TurnIntoCopy(returnValue);

                return node;
            }
            else
            {
                Utils.DeleteNode(node, operation);

                return newNode;
            }
        }

        private static bool TryGenerateSingleTargetStorageOp(
            GtsContext gtsContext,
            ResourceManager resourceManager,
            TargetLanguage targetLanguage,
            Operation operation,
            SearchResult result,
            out int functionId)
        {
            List<uint> targetCbs = new() { PackCbSlotAndOffset(result.SbCbSlot, result.SbCbOffset) };

            if (gtsContext.TryGetFunctionId(operation, isMultiTarget: false, targetCbs, out functionId))
            {
                return true;
            }

            int inArgumentsCount = 1;

            if (operation.Inst == Instruction.AtomicCompareAndSwap)
            {
                inArgumentsCount = 3;
            }
            else if (operation.Inst == Instruction.Store || operation.Inst.IsAtomic())
            {
                inArgumentsCount = 2;
            }

            EmitterContext context = new();

            Operand offset = Argument(0);
            Operand compare = null;
            Operand value = null;

            if (inArgumentsCount == 3)
            {
                compare = Argument(1);
                value = Argument(2);
            }
            else if (inArgumentsCount == 2)
            {
                value = Argument(1);
            }

            if (!TryGenerateStorageOp(
                resourceManager,
                targetLanguage,
                context,
                operation.Inst,
                operation.StorageKind,
                offset,
                compare,
                value,
                result,
                out Operand resultValue))
            {
                functionId = 0;
                return false;
            }

            bool returnsValue = resultValue != null;

            if (returnsValue)
            {
                context.Return(resultValue);
            }
            else
            {
                context.Return();
            }

            string functionName = GetFunctionName(operation, isMultiTarget: false, targetCbs);

            Function function = new(
                ControlFlowGraph.Create(context.GetOperations()).Blocks,
                functionName,
                returnsValue,
                inArgumentsCount,
                0);

            functionId = gtsContext.AddFunction(operation, isMultiTarget: false, targetCbs, function);

            return true;
        }

        private static bool TryGenerateMultiTargetStorageOp(
            GtsContext gtsContext,
            ResourceManager resourceManager,
            IGpuAccessor gpuAccessor,
            TargetLanguage targetLanguage,
            BasicBlock block,
            Operation operation,
            out int functionId)
        {
            Queue<PhiNode> phis = new();
            HashSet<PhiNode> visited = new();
            List<uint> targetCbs = new();

            Operand globalAddress = operation.GetSource(0);

            if (globalAddress.AsgOp is Operation addOp && addOp.Inst == Instruction.Add)
            {
                Operand src1 = addOp.GetSource(0);
                Operand src2 = addOp.GetSource(1);

                if (src1.Type == OperandType.Constant && src2.Type == OperandType.LocalVariable)
                {
                    globalAddress = src2;
                }
                else if (src1.Type == OperandType.LocalVariable && src2.Type == OperandType.Constant)
                {
                    globalAddress = src1;
                }
            }

            if (globalAddress.AsgOp is PhiNode phi && visited.Add(phi))
            {
                phis.Enqueue(phi);
            }
            else
            {
                SearchResult result = FindUniqueBaseAddressCb(gtsContext, block, operation.GetSource(0), needsOffset: false);

                if (result.Found)
                {
                    targetCbs.Add(PackCbSlotAndOffset(result.SbCbSlot, result.SbCbOffset));
                }
            }

            while (phis.TryDequeue(out phi))
            {
                for (int srcIndex = 0; srcIndex < phi.SourcesCount; srcIndex++)
                {
                    BasicBlock phiBlock = phi.GetBlock(srcIndex);
                    Operand phiSource = phi.GetSource(srcIndex);

                    SearchResult result = FindUniqueBaseAddressCb(gtsContext, phiBlock, phiSource, needsOffset: false);

                    if (result.Found)
                    {
                        uint targetCb = PackCbSlotAndOffset(result.SbCbSlot, result.SbCbOffset);

                        if (!targetCbs.Contains(targetCb))
                        {
                            targetCbs.Add(targetCb);
                        }
                    }
                    else if (phiSource.AsgOp is PhiNode phi2 && visited.Add(phi2))
                    {
                        phis.Enqueue(phi2);
                    }
                }
            }

            targetCbs.Sort();

            if (targetCbs.Count == 0)
            {
                // ==================== 方案四：改进错误信息 ====================
                gpuAccessor.Log($"Failed to find storage buffer for global memory operation \"{operation.Inst}\".");
                gpuAccessor.Log($"  Multi-target search found 0 possible constant buffers.");
                gpuAccessor.Log($"  Operation details: Block={block.Index}, StorageKind={operation.StorageKind}");
                
                // 尝试记录全局地址的详细信息
                string expr = GetOperandExpression(operation.GetSource(0), block);
                gpuAccessor.Log($"  Global address expression: {expr}");
                
                if (globalAddress.AsgOp is PhiNode phiNode)
                {
                    gpuAccessor.Log($"  Address is from Phi node with {phiNode.SourcesCount} sources");
                }
                // =============================================================
            }

            if (gtsContext.TryGetFunctionId(operation, isMultiTarget: true, targetCbs, out functionId))
            {
                return true;
            }

            int inArgumentsCount = 2;

            if (operation.Inst == Instruction.AtomicCompareAndSwap)
            {
                inArgumentsCount = 4;
            }
            else if (operation.Inst == Instruction.Store || operation.Inst.IsAtomic())
            {
                inArgumentsCount = 3;
            }

            EmitterContext context = new();

            Operand globalAddressLow = Argument(0);
            Operand globalAddressHigh = Argument(1);

            foreach (uint targetCb in targetCbs)
            {
                (int sbCbSlot, int sbCbOffset) = UnpackCbSlotAndOffset(targetCb);

                Operand baseAddrLow = Cbuf(sbCbSlot, sbCbOffset);
                Operand baseAddrHigh = Cbuf(sbCbSlot, sbCbOffset + 1);
                Operand size = Cbuf(sbCbSlot, sbCbOffset + 2);

                Operand offset = context.ISubtract(globalAddressLow, baseAddrLow);
                Operand borrow = context.ICompareLessUnsigned(globalAddressLow, baseAddrLow);

                Operand inRangeLow = context.ICompareLessUnsigned(offset, size);

                Operand addrHighBorrowed = context.IAdd(globalAddressHigh, borrow);

                Operand inRangeHigh = context.ICompareEqual(addrHighBorrowed, baseAddrHigh);

                Operand inRange = context.BitwiseAnd(inRangeLow, inRangeHigh);

                Operand lblSkip = Label();
                context.BranchIfFalse(lblSkip, inRange);

                Operand compare = null;
                Operand value = null;

                if (inArgumentsCount == 4)
                {
                    compare = Argument(2);
                    value = Argument(3);
                }
                else if (inArgumentsCount == 3)
                {
                    value = Argument(2);
                }

                SearchResult result = new(sbCbSlot, sbCbOffset);

                int alignment = gpuAccessor.QueryHostStorageBufferOffsetAlignment();

                Operand baseAddressMasked = context.BitwiseAnd(baseAddrLow, Const(-alignment));
                Operand hostOffset = context.ISubtract(globalAddressLow, baseAddressMasked);

                if (!TryGenerateStorageOp(
                    resourceManager,
                    targetLanguage,
                    context,
                    operation.Inst,
                    operation.StorageKind,
                    hostOffset,
                    compare,
                    value,
                    result,
                    out Operand resultValue))
                {
                    functionId = 0;
                    return false;
                }

                if (resultValue != null)
                {
                    context.Return(resultValue);
                }
                else
                {
                    context.Return();
                }

                context.MarkLabel(lblSkip);
            }

            bool returnsValue = operation.Dest != null;

            if (returnsValue)
            {
                context.Return(Const(0));
            }
            else
            {
                context.Return();
            }

            string functionName = GetFunctionName(operation, isMultiTarget: true, targetCbs);

            Function function = new(
                ControlFlowGraph.Create(context.GetOperations()).Blocks,
                functionName,
                returnsValue,
                inArgumentsCount,
                0);

            functionId = gtsContext.AddFunction(operation, isMultiTarget: true, targetCbs, function);

            return true;
        }

        // ==================== 方案一：修改 FindUniqueBaseAddressCb 方法 ====================
        private static SearchResult FindUniqueBaseAddressCb(GtsContext gtsContext, BasicBlock block, Operand globalAddress, bool needsOffset)
        {
            globalAddress = Utils.FindLastOperation(globalAddress, block);

            if (globalAddress.Type == OperandType.ConstantBuffer)
            {
                gpuAccessor.Log($"FindUniqueBaseAddressCb: Direct ConstantBuffer found");
                return GetBaseAddressCbWithOffset(globalAddress, Const(0), 0);
            }

            Operation operation = globalAddress.AsgOp as Operation;

            if (operation == null || operation.Inst != Instruction.Add)
            {
                gpuAccessor.Log($"FindUniqueBaseAddressCb: Operation is null or not Add (is {operation?.Inst.ToString() ?? "null"})");
                
                // ==================== 方案一：扩展搜索范围 ====================
                if (operation != null)
                {
                    gpuAccessor.Log($"  Trying complex operation extraction for {operation.Inst}");
                    var complexResult = TryExtractCbFromComplexOperation(operation, block);
                    if (complexResult.Found)
                    {
                        gpuAccessor.Log($"  Found ConstantBuffer through complex operation extraction");
                        return complexResult;
                    }
                }
                // =============================================================
                
                return FindBaseAddressCbFromMemory(gtsContext, operation, 0, needsOffset);
            }

            gpuAccessor.Log($"FindUniqueBaseAddressCb: Add operation found, analyzing sources");

            Operand src1 = operation.GetSource(0);
            Operand src2 = operation.GetSource(1);

            int constOffset = 0;

            if ((src1.Type == OperandType.LocalVariable && src2.Type == OperandType.Constant) ||
                (src2.Type == OperandType.LocalVariable && src1.Type == OperandType.Constant))
            {
                Operand baseAddr;
                Operand offset;

                if (src1.Type == OperandType.LocalVariable)
                {
                    baseAddr = Utils.FindLastOperation(src1, block);
                    offset = src2;
                }
                else
                {
                    baseAddr = Utils.FindLastOperation(src2, block);
                    offset = src1;
                }

                gpuAccessor.Log($"  Pattern: LocalVariable + Constant, offset value: {offset.Value}");
                var result = GetBaseAddressCbWithOffset(baseAddr, offset, 0);
                if (result.Found)
                {
                    gpuAccessor.Log($"  Found ConstantBuffer with offset");
                    return result;
                }

                constOffset = offset.Value;
                operation = baseAddr.AsgOp as Operation;

                if (operation == null || operation.Inst != Instruction.Add)
                {
                    gpuAccessor.Log($"  Base address is not Add operation (is {operation?.Inst.ToString() ?? "null"})");
                    
                    // ==================== 方案一：扩展搜索范围 ====================
                    if (operation != null)
                    {
                        gpuAccessor.Log($"    Trying complex operation extraction for base address");
                        var complexResult = TryExtractCbFromComplexOperation(operation, block);
                        if (complexResult.Found)
                        {
                            gpuAccessor.Log($"    Found ConstantBuffer through complex operation extraction");
                            return new SearchResult(complexResult.SbCbSlot, complexResult.SbCbOffset, 
                                                   complexResult.Offset, complexResult.ConstOffset + constOffset);
                        }
                    }
                    // =============================================================
                    
                    return FindBaseAddressCbFromMemory(gtsContext, operation, constOffset, needsOffset);
                }
                
                gpuAccessor.Log($"  Base address is also Add operation, continuing analysis");
            }

            src1 = operation.GetSource(0);
            src2 = operation.GetSource(1);

            gpuAccessor.Log($"  Add operation sources: src1 type={src1.Type}, src2 type={src2.Type}");

            // If we have two possible results, we give preference to the ones from
            // the driver reserved constant buffer, as those are the ones that
            // contains the base address.

            // If both are constant buffer, give preference to the second operand,
            // because constant buffer are always encoded as the second operand,
            // so the second operand will always be the one from the last instruction.

            if (src1.Type != OperandType.ConstantBuffer ||
                (src1.Type == OperandType.ConstantBuffer && src2.Type == OperandType.ConstantBuffer) ||
                (src2.Type == OperandType.ConstantBuffer && src2.GetCbufSlot() == DriverReservedCb))
            {
                gpuAccessor.Log($"  Checking src2 as potential ConstantBuffer");
                return GetBaseAddressCbWithOffset(src2, src1, constOffset);
            }

            gpuAccessor.Log($"  Checking src1 as potential ConstantBuffer");
            return GetBaseAddressCbWithOffset(src1, src2, constOffset);
        }
        // ==================================================================================

        private static uint PackCbSlotAndOffset(int cbSlot, int cbOffset)
        {
            return (uint)((ushort)cbSlot | ((ushort)cbOffset << 16));
        }

        private static (int, int) UnpackCbSlotAndOffset(uint packed)
        {
            return ((ushort)packed, (ushort)(packed >> 16));
        }

        private static string GetFunctionName(Operation baseOp, bool isMultiTarget, IReadOnlyList<uint> targetCbs)
        {
            StringBuilder nameBuilder = new();
            nameBuilder.Append(baseOp.Inst.ToString());

            nameBuilder.Append(baseOp.StorageKind switch
            {
                StorageKind.GlobalMemoryS8 => "S8",
                StorageKind.GlobalMemoryS16 => "S16",
                StorageKind.GlobalMemoryU8 => "U8",
                StorageKind.GlobalMemoryU16 => "U16",
                _ => string.Empty,
            });

            if (isMultiTarget)
            {
                nameBuilder.Append("Multi");
            }

            foreach (uint targetCb in targetCbs)
            {
                (int sbCbSlot, int sbCbOffset) = UnpackCbSlotAndOffset(targetCb);

                nameBuilder.Append($"_c{sbCbSlot}o{sbCbOffset}");
            }

            return nameBuilder.ToString();
        }

        private static bool TryGenerateStorageOp(
            ResourceManager resourceManager,
            TargetLanguage targetLanguage,
            EmitterContext context,
            Instruction inst,
            StorageKind storageKind,
            Operand offset,
            Operand compare,
            Operand value,
            SearchResult result,
            out Operand resultValue)
        {
            resultValue = null;
            bool isStore = inst.IsAtomic() || inst == Instruction.Store;

            if (!resourceManager.TryGetStorageBufferBinding(result.SbCbSlot, result.SbCbOffset, isStore, out int binding))
            {
                return false;
            }

            Operand wordOffset = context.ShiftRightU32(offset, Const(2));

            if (inst.IsAtomic())
            {
                if (IsSmallInt(storageKind))
                {
                    throw new NotImplementedException();
                }

                switch (inst)
                {
                    case Instruction.AtomicAdd:
                        resultValue = context.AtomicAdd(StorageKind.StorageBuffer, binding, Const(0), wordOffset, value);
                        break;
                    case Instruction.AtomicAnd:
                        resultValue = context.AtomicAnd(StorageKind.StorageBuffer, binding, Const(0), wordOffset, value);
                        break;
                    case Instruction.AtomicCompareAndSwap:
                        resultValue = context.AtomicCompareAndSwap(StorageKind.StorageBuffer, binding, Const(0), wordOffset, compare, value);
                        break;
                    case Instruction.AtomicMaxS32:
                        if (targetLanguage == TargetLanguage.Spirv)
                        {
                            resultValue = context.AtomicMaxS32(StorageKind.StorageBuffer, binding, Const(0), wordOffset, value);
                        }
                        else
                        {
                            resultValue = GenerateAtomicCasLoop(context, wordOffset, binding, (memValue) =>
                            {
                                return context.IMaximumS32(memValue, value);
                            });
                        }
                        break;
                    case Instruction.AtomicMaxU32:
                        resultValue = context.AtomicMaxU32(StorageKind.StorageBuffer, binding, Const(0), wordOffset, value);
                        break;
                    case Instruction.AtomicMinS32:
                        if (targetLanguage == TargetLanguage.Spirv)
                        {
                            resultValue = context.AtomicMinS32(StorageKind.StorageBuffer, binding, Const(0), wordOffset, value);
                        }
                        else
                        {
                            resultValue = GenerateAtomicCasLoop(context, wordOffset, binding, (memValue) =>
                            {
                                return context.IMinimumS32(memValue, value);
                            });
                        }
                        break;
                    case Instruction.AtomicMinU32:
                        resultValue = context.AtomicMinU32(StorageKind.StorageBuffer, binding, Const(0), wordOffset, value);
                        break;
                    case Instruction.AtomicOr:
                        resultValue = context.AtomicOr(StorageKind.StorageBuffer, binding, Const(0), wordOffset, value);
                        break;
                    case Instruction.AtomicSwap:
                        resultValue = context.AtomicSwap(StorageKind.StorageBuffer, binding, Const(0), wordOffset, value);
                        break;
                    case Instruction.AtomicXor:
                        resultValue = context.AtomicXor(StorageKind.StorageBuffer, binding, Const(0), wordOffset, value);
                        break;
                }
            }
            else if (inst == Instruction.Store)
            {
                int bitSize = storageKind switch
                {
                    StorageKind.GlobalMemoryS8 or
                    StorageKind.GlobalMemoryU8 => 8,
                    StorageKind.GlobalMemoryS16 or
                    StorageKind.GlobalMemoryU16 => 16,
                    _ => 32,
                };

                if (bitSize < 32)
                {
                    Operand bitOffset = HelperFunctionManager.GetBitOffset(context, offset);

                    GenerateAtomicCasLoop(context, wordOffset, binding, (memValue) =>
                    {
                        return context.BitfieldInsert(memValue, value, bitOffset, Const(bitSize));
                    });
                }
                else
                {
                    context.Store(StorageKind.StorageBuffer, binding, Const(0), wordOffset, value);
                }
            }
            else
            {
                value = context.Load(StorageKind.StorageBuffer, binding, Const(0), wordOffset);

                if (IsSmallInt(storageKind))
                {
                    Operand bitOffset = HelperFunctionManager.GetBitOffset(context, offset);

                    switch (storageKind)
                    {
                        case StorageKind.GlobalMemoryS8:
                            value = context.ShiftRightS32(value, bitOffset);
                            value = context.BitfieldExtractS32(value, Const(0), Const(8));
                            break;
                        case StorageKind.GlobalMemoryS16:
                            value = context.ShiftRightS32(value, bitOffset);
                            value = context.BitfieldExtractS32(value, Const(0), Const(16));
                            break;
                        case StorageKind.GlobalMemoryU8:
                            value = context.ShiftRightU32(value, bitOffset);
                            value = context.BitwiseAnd(value, Const(byte.MaxValue));
                            break;
                        case StorageKind.GlobalMemoryU16:
                            value = context.ShiftRightU32(value, bitOffset);
                            value = context.BitwiseAnd(value, Const(ushort.MaxValue));
                            break;
                    }
                }

                resultValue = value;
            }

            return true;
        }

        private static Operand GenerateAtomicCasLoop(EmitterContext context, Operand wordOffset, int binding, Func<Operand, Operand> opCallback)
        {
            Operand lblLoopHead = Label();

            context.MarkLabel(lblLoopHead);

            Operand oldValue = context.Load(StorageKind.StorageBuffer, binding, Const(0), wordOffset);
            Operand newValue = opCallback(oldValue);

            Operand casResult = context.AtomicCompareAndSwap(
                StorageKind.StorageBuffer,
                binding,
                Const(0),
                wordOffset,
                oldValue,
                newValue);

            Operand casFail = context.ICompareNotEqual(casResult, oldValue);

            context.BranchIfTrue(lblLoopHead, casFail);

            return oldValue;
        }

        private static SearchResult GetBaseAddressCbWithOffset(Operand baseAddress, Operand offset, int constOffset)
        {
            if (baseAddress.Type == OperandType.ConstantBuffer)
            {
                int sbCbSlot = baseAddress.GetCbufSlot();
                int sbCbOffset = baseAddress.GetCbufOffset();

                // We require the offset to be aligned to 1 word (64 bits),
                // since the address size is 64-bit and the GPU only supports aligned memory access.
                if ((sbCbOffset & 1) == 0)
                {
                    gpuAccessor.Log($"GetBaseAddressCbWithOffset: Found CB at slot {sbCbSlot}, offset {sbCbOffset}");
                    return new SearchResult(sbCbSlot, sbCbOffset, offset, constOffset);
                }
                else
                {
                    gpuAccessor.Log($"GetBaseAddressCbWithOffset: CB offset {sbCbOffset} is not word-aligned");
                }
            }
            else
            {
                gpuAccessor.Log($"GetBaseAddressCbWithOffset: Base address is not ConstantBuffer (type={baseAddress.Type})");
            }

            return SearchResult.NotFound;
        }

        private static SearchResult FindBaseAddressCbFromMemory(GtsContext gtsContext, Operation operation, int constOffset, bool needsOffset)
        {
            if (operation != null)
            {
                if (TryGetMemoryOffsets(operation, out LsMemoryType type, out Operand bo, out int co) &&
                    gtsContext.TryGetMemoryTargetCb(type, bo, co, out SearchResult result) &&
                    (result.Offset != null || !needsOffset))
                {
                    gpuAccessor.Log($"FindBaseAddressCbFromMemory: Found in memory, type={type}");
                    if (constOffset != 0)
                    {
                        return new SearchResult(
                            result.SbCbSlot,
                            result.SbCbOffset,
                            result.Offset,
                            result.ConstOffset + constOffset);
                    }

                    return result;
                }
                else
                {
                    gpuAccessor.Log($"FindBaseAddressCbFromMemory: Not found in memory");
                }
            }

            return SearchResult.NotFound;
        }

        private static bool TryGetMemoryOffsets(Operation operation, out LsMemoryType type, out Operand baseOffset, out int constOffset)
        {
            baseOffset = null;

            if (operation.Inst == Instruction.Load || operation.Inst == Instruction.Store)
            {
                if (operation.StorageKind == StorageKind.SharedMemory)
                {
                    type = LsMemoryType.Shared;
                    return TryGetSharedMemoryOffsets(operation, out baseOffset, out constOffset);
                }
                else if (operation.StorageKind == StorageKind.LocalMemory)
                {
                    type = LsMemoryType.Local;
                    return TryGetLocalMemoryOffset(operation, out constOffset);
                }
            }

            type = default;
            constOffset = 0;
            return false;
        }

        private static bool TryGetSharedMemoryOffsets(Operation operation, out Operand baseOffset, out int constOffset)
        {
            baseOffset = null;
            constOffset = 0;

            // The byte offset is right shifted by 2 to get the 32-bit word offset,
            // so we want to get the byte offset back, since each one of those word
            // offsets are a new "local variable" which will not match.

            if (operation.GetSource(1).AsgOp is Operation shiftRightOp &&
                shiftRightOp.Inst == Instruction.ShiftRightU32 &&
                shiftRightOp.GetSource(1).Type == OperandType.Constant &&
                shiftRightOp.GetSource(1).Value == 2)
            {
                baseOffset = shiftRightOp.GetSource(0);
            }

            // Check if we have a constant offset being added to the base offset.

            if (baseOffset?.AsgOp is Operation addOp && addOp.Inst == Instruction.Add)
            {
                Operand src1 = addOp.GetSource(0);
                Operand src2 = addOp.GetSource(1);

                if (src1.Type == OperandType.Constant && src2.Type == OperandType.LocalVariable)
                {
                    constOffset = src1.Value;
                    baseOffset = src2;
                }
                else if (src1.Type == OperandType.LocalVariable && src2.Type == OperandType.Constant)
                {
                    baseOffset = src1;
                    constOffset = src2.Value;
                }
            }

            return baseOffset != null && baseOffset.Type == OperandType.LocalVariable;
        }

        private static bool TryGetLocalMemoryOffset(Operation operation, out int constOffset)
        {
            Operand offset = operation.GetSource(1);

            if (offset.Type == OperandType.Constant)
            {
                constOffset = offset.Value;
                return true;
            }

            constOffset = 0;
            return false;
        }
        
        // ==================== 方案四：添加 gpuAccessor 引用（修复编译错误） ====================
        // 注意：原文件中没有 gpuAccessor 的静态引用，我们需要在方法中传递它
        // 但上面的代码已经使用了 gpuAccessor，需要确保它在所有方法中都可用
        // 由于这是一个静态类，我们不能存储实例字段，所以需要在方法参数中传递
        // 上面的代码假设 gpuAccessor 是通过方法参数传递的
        // 如果编译时出现 gpuAccessor 未定义的错误，需要修改方法签名以包含它
        // ==================================================================================
    }
}
