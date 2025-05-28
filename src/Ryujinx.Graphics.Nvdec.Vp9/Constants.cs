}
                    break;
                case 3:
                    if (bMode == PredictionMode.NearestMv)
                    {
                        bestSub8x8 = bmi[2].Mv[refr];
                    }
                    else
                    {
                        Span<Mv> candidates = stackalloc Mv[2 + Constants.MaxMvRefCandidates];
                        candidates[0] = bmi[1].Mv[refr];
                        candidates[1] = bmi[0].Mv[refr];
                        candidates[2] = mvList[0];
                        candidates[3] = mvList[1];
                        bestSub8x8 = new Mv();
                        for (n = 0; n < 2 + Constants.MaxMvRefCandidates; ++n)
                        {
                            if (Unsafe.As<Mv, int>(ref bmi[2].Mv[refr]) != Unsafe.As<Mv, int>(ref candidates[n]))
                            {
                                bestSub8x8 = candidates[n];
                                break;
                            }
                        }
                    }
                    break;
                default:
                    Debug.Assert(false, "Invalid block index.");
                    break;
            }
        }

        private static byte GetModeContext(ref Vp9Common cm, ref MacroBlockD xd, Span<Position> mvRefSearch, int miRow, int miCol)
        {
            int i;
            int contextCounter = 0;
            ref TileInfo tile = ref xd.Tile;

            // Get mode count from nearest 2 blocks
            for (i = 0; i < 2; ++i)
            {
                ref Position mvRef = ref mvRefSearch[i];
                if (tile.IsInside(miCol, miRow, cm.MiRows, ref mvRef))
                {
                    ref ModeInfo candidate = ref xd.Mi[mvRef.Col + mvRef.Row * xd.MiStride].Value;
                    // Keep counts for entropy encoding.
                    contextCounter += Luts.Mode2Counter[(int)candidate.Mode];
                }
            }

            return (byte)Luts.CounterToContext[contextCounter];
        }

        private static void ReadInterBlockModeInfo(
            ref Vp9Common cm,
            ref MacroBlockD xd,
            ref ModeInfo mi,
            int miRow,
            int miCol,
            ref Reader r)
        {
            BlockSize bsize = mi.SbType;
            bool allowHP = cm.AllowHighPrecisionMv;
            Array2<Mv> bestRefMvs = new();
            int refr, isCompound;
            byte interModeCtx;
            Span<Position> mvRefSearch = Luts.MvRefBlocks[(int)bsize];

            ReadRefFrames(ref cm, ref xd, ref r, mi.SegmentId, ref mi.RefFrame);
            isCompound = mi.HasSecondRef() ? 1 : 0;
            interModeCtx = GetModeContext(ref cm, ref xd, mvRefSearch, miRow, miCol);

            if (cm.Seg.IsSegFeatureActive(mi.SegmentId, SegLvlFeatures.SegLvlSkip) != 0)
            {
                mi.Mode = PredictionMode.ZeroMv;
                if (bsize < BlockSize.Block8x8)
                {
                    xd.ErrorInfo.Value.InternalError(CodecErr.CodecUnsupBitstream, "Invalid usage of segement feature on small blocks");

                    return;
                }
            }
            else
            {
                if (bsize >= BlockSize.Block8x8)
                {
                    mi.Mode = ReadInterMode(ref cm, ref xd, ref r, interModeCtx);
                }
                else
                {
                    // Sub 8x8 blocks use the nearestmv as a ref_mv if the bMode is NewMv.
                    // Setting mode to NearestMv forces the search to stop after the nearestmv
                    // has been found. After bModes have been read, mode will be overwritten
                    // by the last bMode.
                    mi.Mode = PredictionMode.NearestMv;
                }

                if (mi.Mode != PredictionMode.ZeroMv)
                {
                    Span<Mv> tmpMvs = stackalloc Mv[Constants.MaxMvRefCandidates];

                    for (refr = 0; refr < 1 + isCompound; ++refr)
                    {
                        sbyte frame = mi.RefFrame[refr];
                        int refmvCount;

                        refmvCount = DecFindMvRefs(ref cm, ref xd, mi.Mode, frame, mvRefSearch, tmpMvs, miRow, miCol, -1, 0);

                        DecFindBestRefMvs(allowHP, tmpMvs, ref bestRefMvs[refr], refmvCount);
                    }
                }
            }

            mi.InterpFilter = (cm.InterpFilter == Constants.Switchable) ? ReadSwitchableInterpFilter(ref cm, ref xd, ref r) : cm.InterpFilter;

            if (bsize < BlockSize.Block8x8)
            {
                int num4X4W = 1 << xd.BmodeBlocksWl;
                int num4X4H = 1 << xd.BmodeBlocksHl;
                int idx, idy;
                PredictionMode bMode = 0;
                Array2<Mv> bestSub8x8 = new();
                const uint InvalidMv = 0x80008000;
                // Initialize the 2nd element as even though it won't be used meaningfully
                // if isCompound is false.
                Unsafe.As<Mv, uint>(ref bestSub8x8[1]) = InvalidMv;
                for (idy = 0; idy < 2; idy += num4X4H)
                {
                    for (idx = 0; idx < 2; idx += num4X4W)
                    {
                        int j = idy * 2 + idx;
                        bMode = ReadInterMode(ref cm, ref xd, ref r, interModeCtx);

                        if (bMode == PredictionMode.NearestMv || bMode == PredictionMode.NearMv)
                        {
                            for (refr = 0; refr < 1 + isCompound; ++refr)
                            {
                                AppendSub8x8MvsForIdx(ref cm, ref xd, mvRefSearch, bMode, j, refr, miRow, miCol, ref bestSub8x8[refr]);
                            }
                        }

                        if (!AssignMv(ref cm, ref xd, bMode, ref mi.Bmi[j].Mv, ref bestRefMvs, ref bestSub8x8, isCompound, allowHP, ref r))
                        {
                            xd.Corrupted |= true;
                            break;
                        }

                        if (num4X4H == 2)
                        {
                            mi.Bmi[j + 2] = mi.Bmi[j];
                        }

                        if (num4X4W == 2)
                        {
                            mi.Bmi[j + 1] = mi.Bmi[j];
                        }
                    }
                }

                mi.Mode = bMode;

                CopyMvPair(ref mi.Mv, ref mi.Bmi[3].Mv);
            }
            else
            {
                xd.Corrupted |= !AssignMv(ref cm, ref xd, mi.Mode, ref mi.Mv, ref bestRefMvs, ref bestRefMvs, isCompound, allowHP, ref r);
            }
        }

        private static void ReadInterFrameModeInfo(
            ref Vp9Common cm,
            ref MacroBlockD xd,
            int miRow,
            int miCol,
            ref Reader r,
            int xMis,
            int yMis)
        {
            ref ModeInfo mi = ref xd.Mi[0].Value;
            bool interBlock;

            mi.SegmentId = (sbyte)ReadInterSegmentId(ref cm, ref xd, miRow, miCol, ref r, xMis, yMis);
            mi.Skip = (sbyte)ReadSkip(ref cm, ref xd, mi.SegmentId, ref r);
            interBlock = ReadIsInterBlock(ref cm, ref xd, mi.SegmentId, ref r);
            mi.TxSize = ReadTxSize(ref cm, ref xd, mi.Skip == 0 || !interBlock, ref r);

            if (interBlock)
            {
                ReadInterBlockModeInfo(ref cm, ref xd, ref mi, miRow, miCol, ref r);
            }
            else
            {
                ReadIntraBlockModeInfo(ref cm, ref xd, ref mi, ref r);
            }
        }

        private static PredictionMode LeftBlockMode(Ptr<ModeInfo> curMi, Ptr<ModeInfo> leftMi, int b)
        {
            if (b == 0 || b == 2)
            {
                if (leftMi.IsNull || leftMi.Value.IsInterBlock())
                {
                    return PredictionMode.DcPred;
                }

                return leftMi.Value.GetYMode(b + 1);
            }

            Debug.Assert(b == 1 || b == 3);

            return curMi.Value.Bmi[b - 1].Mode;
        }

        private static PredictionMode AboveBlockMode(Ptr<ModeInfo> curMi, Ptr<ModeInfo> aboveMi, int b)
        {
            if (b == 0 || b == 1)
            {
                if (aboveMi.IsNull || aboveMi.Value.IsInterBlock())
                {
                    return PredictionMode.DcPred;
                }

                return aboveMi.Value.GetYMode(b + 2);
            }

            Debug.Assert(b == 2 || b == 3);

            return curMi.Value.Bmi[b - 2].Mode;
        }

        private static ReadOnlySpan<byte> GetYModeProbs(
            ref Vp9EntropyProbs fc,
            Ptr<ModeInfo> mi,
            Ptr<ModeInfo> aboveMi,
            Ptr<ModeInfo> leftMi,
            int block)
        {
            PredictionMode above = AboveBlockMode(mi, aboveMi, block);
            PredictionMode left = LeftBlockMode(mi, leftMi, block);

            return fc.KfYModeProb[(int)above][(int)left].AsSpan();
        }

        private static void ReadIntraFrameModeInfo(
            ref Vp9Common cm,
            ref MacroBlockD xd,
            int miRow,
            int miCol,
            ref Reader r,
            int xMis,
            int yMis)
        {
            Ptr<ModeInfo> mi = xd.Mi[0];
            Ptr<ModeInfo> aboveMi = xd.AboveMi;
            Ptr<ModeInfo> leftMi = xd.LeftMi;
            BlockSize bsize = mi.Value.SbType;
            int i;
            int miOffset = miRow * cm.MiCols + miCol;

            mi.Value.SegmentId = (sbyte)ReadIntraSegmentId(ref cm, miOffset, xMis, yMis, ref r);
            mi.Value.Skip = (sbyte)ReadSkip(ref cm, ref xd, mi.Value.SegmentId, ref r);
            mi.Value.TxSize = ReadTxSize(ref cm, ref xd, true, ref r);
            mi.Value.RefFrame[0] = Constants.IntraFrame;
            mi.Value.RefFrame[1] = Constants.None;

            switch (bsize)
            {
                case BlockSize.Block4x4:
                    for (i = 0; i < 4; ++i)
                    {
                        mi.Value.Bmi[i].Mode =
                            ReadIntraMode(ref r, GetYModeProbs(ref cm.Fc.Value, mi, aboveMi, leftMi, i));
                    }

                    mi.Value.Mode = mi.Value.Bmi[3].Mode;
                    break;
                case BlockSize.Block4x8:
                    mi.Value.Bmi[0].Mode = mi.Value.Bmi[2].Mode =
                        ReadIntraMode(ref r, GetYModeProbs(ref cm.Fc.Value, mi, aboveMi, leftMi, 0));
                    mi.Value.Bmi[1].Mode = mi.Value.Bmi[3].Mode = mi.Value.Mode =
                        ReadIntraMode(ref r, GetYModeProbs(ref cm.Fc.Value, mi, aboveMi, leftMi, 1));
                    break;
                case BlockSize.Block8x4:
                    mi.Value.Bmi[0].Mode = mi.Value.Bmi[1].Mode =
                        ReadIntraMode(ref r, GetYModeProbs(ref cm.Fc.Value, mi, aboveMi, leftMi, 0));
                    mi.Value.Bmi[2].Mode = mi.Value.Bmi[3].Mode = mi.Value.Mode =
                        ReadIntraMode(ref r, GetYModeProbs(ref cm.Fc.Value, mi, aboveMi, leftMi, 2));
                    break;
                default:
                    mi.Value.Mode = ReadIntraMode(ref r, GetYModeProbs(ref cm.Fc.Value, mi, aboveMi, leftMi, 0));
                    break;
            }

            mi.Value.UvMode = ReadIntraMode(ref r, cm.Fc.Value.KfUvModeProb[(int)mi.Value.Mode].AsSpan());
        }

        private static void CopyRefFramePair(ref Array2<sbyte> dst, ref Array2<sbyte> src)
        {
            dst[0] = src[0];
            dst[1] = src[1];
        }

        public static void ReadModeInfo(
            ref TileWorkerData twd,
            ref Vp9Common cm,
            int miRow,
            int miCol,
            int xMis,
            int yMis)
        {
            ref Reader r = ref twd.BitReader;
            ref MacroBlockD xd = ref twd.Xd;
            ref ModeInfo mi = ref xd.Mi[0].Value;
            ArrayPtr<MvRef> frameMvs = cm.CurFrameMvs.Slice(miRow * cm.MiCols + miCol);
            int w, h;

            if (cm.FrameIsIntraOnly())
            {
                ReadIntraFrameModeInfo(ref cm, ref xd, miRow, miCol, ref r, xMis, yMis);
            }
            else
            {
                ReadInterFrameModeInfo(ref cm, ref xd, miRow, miCol, ref r, xMis, yMis);

                for (h = 0; h < yMis; ++h)
                {
                    for (w = 0; w < xMis; ++w)
                    {
                        ref MvRef mv = ref frameMvs[w];
                        CopyRefFramePair(ref mv.RefFrame, ref mi.RefFrame);
                        CopyMvPair(ref mv.Mv, ref mi.Mv);
                    }
                    frameMvs = frameMvs.Slice(cm.MiCols);
                }
            }
        }
    }
}
