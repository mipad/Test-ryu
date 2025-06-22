using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using System;
using System.Linq;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class Translator
    {
        private const int ThreadsPerWarp = 32;
        private const int HeaderSize = 0x50;

        internal readonly struct FunctionCode
        {
            public Operation[] Code { get; }

            public FunctionCode(Operation[] code)
            {
                Code = code;
            }
        }

        public static TranslatorContext CreateContext(ulong address, IGpuAccessor gpuAccessor, TranslationOptions options)
        {
            return DecodeShader(address, gpuAccessor, options);
        }

        private static TranslatorContext DecodeShader(ulong address, IGpuAccessor gpuAccessor, TranslationOptions options)
        {
            int localMemorySize;
            ShaderDefinitions definitions;
            DecodedProgram program;

            if (options.Flags.HasFlag(TranslationFlags.Compute))
            {
                definitions = CreateComputeDefinitions(gpuAccessor);
                localMemorySize = gpuAccessor.QueryComputeLocalMemorySize();

                program = Decoder.Decode(definitions, gpuAccessor, address);
            }
            else
            {
                ShaderHeader header = new(gpuAccessor, address);

                definitions = CreateGraphicsDefinitions(gpuAccessor, header);
                localMemorySize = GetLocalMemorySize(header);

                program = Decoder.Decode(definitions, gpuAccessor, address + HeaderSize);
            }

            ulong maxEndAddress = 0;

            foreach (DecodedFunction function in program)
            {
                foreach (Block block in function.Blocks)
                {
                    if (maxEndAddress < block.EndAddress)
                    {
                        maxEndAddress = block.EndAddress;
                    }
                }
            }

            int size = (int)maxEndAddress + (options.Flags.HasFlag(TranslationFlags.Compute) ? 0 : HeaderSize);

            return new TranslatorContext(address, size, localMemorySize, definitions, gpuAccessor, options, program);
        }

        private static ShaderDefinitions CreateComputeDefinitions(IGpuAccessor gpuAccessor)
        {
            return new ShaderDefinitions(
                ShaderStage.Compute,
                gpuAccessor.QueryComputeLocalSizeX(),
                gpuAccessor.QueryComputeLocalSizeY(),
                gpuAccessor.QueryComputeLocalSizeZ());
        }

        private static ShaderDefinitions CreateGraphicsDefinitions(IGpuAccessor gpuAccessor, ShaderHeader header)
        {
            TransformFeedbackOutput[] transformFeedbackOutputs = GetTransformFeedbackOutputs(gpuAccessor, out ulong transformFeedbackVecMap);

            var definitions = new ShaderDefinitions(
                header.Stage,
                gpuAccessor.QueryGraphicsState(),
                header.Stage == ShaderStage.Geometry && header.GpPassthrough,
                header.ThreadsPerInputPrimitive,
                header.OutputTopology,
                header.MaxOutputVertexCount,
                header.ImapTypes,
                header.OmapTargets,
                header.OmapSampleMask,
                header.OmapDepth,
                gpuAccessor.QueryHostSupportsScaledVertexFormats(),
                transformFeedbackVecMap,
                transformFeedbackOutputs);

            definitions.ForcePassthrough = gpuAccessor.QueryForcePassthrough();

            return definitions;
        }

        internal static TransformFeedbackOutput[] GetTransformFeedbackOutputs(IGpuAccessor gpuAccessor, out ulong transformFeedbackVecMap)
        {
            bool transformFeedbackEnabled =
                gpuAccessor.QueryTransformFeedbackEnabled() &&
                gpuAccessor.QueryHostSupportsTransformFeedback();
            TransformFeedbackOutput[] transformFeedbackOutputs = null;
            transformFeedbackVecMap = 0UL;

            if (transformFeedbackEnabled)
            {
                transformFeedbackOutputs = new TransformFeedbackOutput[0xc0];

                for (int tfbIndex = 0; tfbIndex < 4; tfbIndex++)
                {
                    var locations = gpuAccessor.QueryTransformFeedbackVaryingLocations(tfbIndex);
                    var stride = gpuAccessor.QueryTransformFeedbackStride(tfbIndex);

                    for (int i = 0; i < locations.Length; i++)
                    {
                        byte wordOffset = locations[i];
                        if (wordOffset < 0xc0)
                        {
                            transformFeedbackOutputs[wordOffset] = new TransformFeedbackOutput(tfbIndex, i * 4, stride);
                            transformFeedbackVecMap |= 1UL << (wordOffset / 4);
                        }
                    }
                }
            }

            return transformFeedbackOutputs;
        }

        private static int GetLocalMemorySize(ShaderHeader header)
        {
            return header.ShaderLocalMemoryLowSize + header.ShaderLocalMemoryHighSize + (header.ShaderLocalMemoryCrsSize / ThreadsPerWarp);
        }

        internal static FunctionCode[] EmitShader(
            TranslatorContext translatorContext,
            ResourceManager resourceManager,
            DecodedProgram program,
            bool vertexAsCompute,
            bool initializeOutputs,
            out int initializationOperations)
        {
            initializationOperations = 0;

            FunctionMatch.RunPass(program);

            foreach (DecodedFunction function in program.Where(x => !x.IsCompilerGenerated).OrderBy(x => x.Address))
            {
                program.AddFunctionAndSetId(function);
            }

            FunctionCode[] functions = new FunctionCode[program.FunctionsWithIdCount];

            for (int index = 0; index < functions.Length; index++)
            {
                EmitterContext context = new(translatorContext, resourceManager, program, vertexAsCompute, index != 0);

                if (initializeOutputs && index == 0)
                {
                    EmitOutputsInitialization(context, translatorContext.AttributeUsage, translatorContext.GpuAccessor, translatorContext.Stage);
                    initializationOperations = context.OperationsCount;
                }

                DecodedFunction function = program.GetFunctionById(index);

                foreach (Block block in function.Blocks)
                {
                    context.CurrBlock = block;

                    context.EnterBlock(block.Address);

                    EmitOps(context, block);
                }

                functions[index] = new(context.GetOperations());
            }

            return functions;
        }

        private static void EmitOutputsInitialization(EmitterContext context, AttributeUsage attributeUsage, IGpuAccessor gpuAccessor, ShaderStage stage)
        {
            if (stage == ShaderStage.Compute || stage == ShaderStage.Fragment)
            {
                return;
            }

            if (stage == ShaderStage.Vertex)
            {
                InitializeVertexOutputs(context);
            }

            UInt128 usedAttributes = context.TranslatorContext.AttributeUsage.NextInputAttributesComponents;
            while (usedAttributes != UInt128.Zero)
            {
                int index = (int)UInt128.TrailingZeroCount(usedAttributes);
                int vecIndex = index / 4;

                usedAttributes &= ~(UInt128.One << index);

                if ((context.TranslatorContext.AttributeUsage.PassthroughAttributes & (1 << vecIndex)) != 0)
                {
                    continue;
                }

                InitializeOutputComponent(context, vecIndex, index & 3, perPatch: false);
            }

            if (context.TranslatorContext.AttributeUsage.NextUsedInputAttributesPerPatch != null)
            {
                foreach (int vecIndex in context.TranslatorContext.AttributeUsage.NextUsedInputAttributesPerPatch.Order())
                {
                    InitializeOutput(context, vecIndex, perPatch: true);
                }
            }

            if (attributeUsage.NextUsesFixedFuncAttributes)
            {
                bool supportsLayerFromVertexOrTess = gpuAccessor.QueryHostSupportsLayerVertexTessellation();
                int fixedStartAttr = supportsLayerFromVertexOrTess ? 0 : 1;

                for (int i = fixedStartAttr; i < fixedStartAttr + 5 + AttributeConsts.TexCoordCount; i++)
                {
                    int index = attributeUsage.GetFreeUserAttribute(isOutput: true, i);
                    if (index < 0)
                    {
                        break;
                    }

                    InitializeOutput(context, index, perPatch: false);
                }
            }
        }

        private static void InitializeVertexOutputs(EmitterContext context)
        {
            for (int c = 0; c < 4; c++)
            {
                context.Store(StorageKind.Output, IoVariable.Position, null, Const(c), ConstF(c == 3 ? 1f : 0f));
            }

            if (context.Program.ClipDistancesWritten != 0)
            {
                for (int i = 0; i < 8; i++)
                {
                    context.Store(StorageKind.Output, IoVariable.ClipDistance, null, Const(i), ConstF(0f));
                }
            }
        }

        private static void InitializeOutput(EmitterContext context, int location, bool perPatch)
        {
            for (int c = 0; c < 4; c++)
            {
                InitializeOutputComponent(context, location, c, perPatch);
            }
        }

        private static void InitializeOutputComponent(EmitterContext context, int location, int c, bool perPatch)
        {
            StorageKind storageKind = perPatch ? StorageKind.OutputPerPatch : StorageKind.Output;
            
            // 强制直通模式：直接将输入传递给输出
            if (context.TranslatorContext.Definitions.ForcePassthrough)
            {
                Operand inputValue = context.Load(StorageKind.Input, IoVariable.UserDefined, null, Const(location), Const(c));
                context.Store(storageKind, IoVariable.UserDefined, null, Const(location), Const(c), inputValue);
                return;
            }

            // 原有逻辑
            IoVariable outputVariable = context.TranslatorContext.GpuAccessor.QueryHostSupportsUserDefined() ? 
                                      IoVariable.UserDefined : 
                                      IoVariable.TextureCoord;

            if (context.TranslatorContext.Definitions.OaIndexing)
            {
                Operand invocationId = null;

                if (context.TranslatorContext.Definitions.Stage == ShaderStage.TessellationControl && !perPatch)
                {
                    invocationId = context.Load(StorageKind.Input, IoVariable.InvocationId);
                }

                int index = location * 4 + c;

                context.Store(storageKind, outputVariable, invocationId, Const(index), ConstF(c == 3 ? 1f : 0f));
            }
            else
            {
                if (context.TranslatorContext.Definitions.Stage == ShaderStage.TessellationControl && !perPatch)
                {
                    Operand invocationId = context.Load(StorageKind.Input, IoVariable.InvocationId);
                    context.Store(storageKind, outputVariable, Const(location), invocationId, Const(c), ConstF(c == 3 ? 1f : 0f));
                }
                else
                {
                    context.Store(storageKind, outputVariable, null, Const(location), Const(c), ConstF(c == 3 ? 1f : 0f));
                }
            }
        }

        private static void EmitOps(EmitterContext context, Block block)
        {
            for (int opIndex = 0; opIndex < block.OpCodes.Count; opIndex++)
            {
                InstOp op = block.OpCodes[opIndex];

                if (context.TranslatorContext.Options.Flags.HasFlag(TranslationFlags.DebugMode))
                {
                    string instName = op.Emitter != null ? op.Name.ToString() : "???";
                    context.Add(new CommentNode($"0x{op.Address:X6}: 0x{op.RawOpCode:X16} {instName}"));

                    if (op.Emitter == null)
                    {
                        context.TranslatorContext.GpuAccessor.Log($"Invalid instruction at 0x{op.Address:X6} (0x{op.RawOpCode:X16}).");
                        return;
                    }
                }

                InstConditional opConditional = new(op.RawOpCode);

                bool noPred = op.Props.HasFlag(InstProps.NoPred);
                if (!noPred && opConditional.Pred == RegisterConsts.PredicateTrueIndex && opConditional.PredInv)
                {
                    continue;
                }

                Operand predSkipLbl = null;

                if (Decoder.IsPopBranch(op.Name))
                {
                    noPred = block.SyncTargets.Count <= 1;
                }
                else if (op.Name == InstName.Bra)
                {
                    noPred = true;
                }

                if (!(opConditional.Pred == RegisterConsts.PredicateTrueIndex || noPred))
                {
                    Operand label = opIndex == block.OpCodes.Count - 1 && block.HasNext() ? 
                                  context.GetLabel(block.Successors[0].Address) : 
                                  Label();

                    predSkipLbl = label;

                    Operand pred = Register(opConditional.Pred, RegisterType.Predicate);
                    if (opConditional.PredInv)
                    {
                        context.BranchIfTrue(label, pred);
                    }
                    else
                    {
                        context.BranchIfFalse(label, pred);
                    }
                }

                context.CurrOp = op;
                op.Emitter?.Invoke(context);

                if (predSkipLbl != null)
                {
                    context.MarkLabel(predSkipLbl);
                }
            }
        }
    }
}
