using LibHac;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using Ryujinx.HLE.FileSystem;
using System.IO;

namespace Ryujinx.HLE.Utilities
{
    public static class PartitionFileSystemUtils
    {
        public static IFileSystem OpenApplicationFileSystem(string path, bool isXci, VirtualFileSystem fileSystem, bool throwOnFailure = true)
        {
            // 不要在这里创建 FileStream，而是在内部方法中处理
            return OpenApplicationFileSystem(path, isXci, fileSystem, throwOnFailure, true);
        }

        private static IFileSystem OpenApplicationFileSystem(string path, bool isXci, VirtualFileSystem fileSystem, bool throwOnFailure, bool openFile)
        {
            if (openFile)
            {
                using FileStream file = File.OpenRead(path);
                return OpenApplicationFileSystem(file, isXci, fileSystem, throwOnFailure);
            }
            else
            {
                // 这个路径不应该被调用，只是为了保持方法签名一致
                return null;
            }
        }

        public static IFileSystem OpenApplicationFileSystem(Stream stream, bool isXci, VirtualFileSystem fileSystem, bool throwOnFailure = true)
        {
            IFileSystem partitionFileSystem;

            if (isXci)
            {
                // 创建 Xci 对象，但不让它在析构时关闭流
                var xci = new Xci(fileSystem.KeySet, stream.AsStorage());
                partitionFileSystem = xci.OpenPartition(XciPartitionType.Secure);
            }
            else
            {
                var pfsTemp = new PartitionFileSystem();
                Result initResult = pfsTemp.Initialize(stream.AsStorage());

                if (throwOnFailure)
                {
                    initResult.ThrowIfFailure();
                }
                else if (initResult.IsFailure())
                {
                    return null;
                }

                partitionFileSystem = pfsTemp;
            }

            fileSystem.ImportTickets(partitionFileSystem);

            return partitionFileSystem;
        }
    }
}
