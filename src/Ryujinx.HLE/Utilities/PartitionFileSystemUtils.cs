using LibHac;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using Ryujinx.HLE.FileSystem;
using System;
using System.IO;

namespace Ryujinx.HLE.Utilities
{
    public static class PartitionFileSystemUtils
    {
        public static IFileSystem OpenApplicationFileSystem(string path, bool isXci, VirtualFileSystem fileSystem, bool throwOnFailure = true)
        {
            using FileStream file = File.OpenRead(path);
            return OpenApplicationFileSystem(file, isXci, fileSystem, throwOnFailure);
        }

        public static IFileSystem OpenApplicationFileSystem(Stream stream, bool isXci, VirtualFileSystem fileSystem, bool throwOnFailure = true)
        {
            IFileSystem partitionFileSystem;

            if (isXci)
            {
                // 使用正确的 Xci 构造函数
                var xci = new Xci(fileSystem.KeySet, stream.AsStorage());
                partitionFileSystem = xci.OpenPartition(XciPartitionType.Secure);
                
                // 由于 Xci 会复制存储内容，我们可以在这里关闭原始流
                // 但为了安全起见，我们不会在这里关闭它
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
        
        // 添加一个重载方法，用于处理需要保持流打开的情况
        public static IFileSystem OpenApplicationFileSystem(Stream stream, bool isXci, VirtualFileSystem fileSystem, bool throwOnFailure, bool keepStreamOpen)
        {
            // 如果不需要保持流打开，使用常规方法
            if (!keepStreamOpen)
            {
                return OpenApplicationFileSystem(stream, isXci, fileSystem, throwOnFailure);
            }
            
            // 如果需要保持流打开，创建一个包装器来管理流
            return new StreamKeepingFileSystem(stream, isXci, fileSystem, throwOnFailure);
        }
        
        // 内部类，用于管理需要保持打开的流
        private class StreamKeepingFileSystem : IFileSystem, IDisposable
        {
            private readonly Stream _stream;
            private readonly IFileSystem _baseFileSystem;
            private bool _disposed = false;
            
            public StreamKeepingFileSystem(Stream stream, bool isXci, VirtualFileSystem fileSystem, bool throwOnFailure)
            {
                _stream = stream;
                _baseFileSystem = PartitionFileSystemUtils.OpenApplicationFileSystem(stream, isXci, fileSystem, throwOnFailure);
            }
            
            public void Dispose()
            {
                if (!_disposed)
                {
                    (_baseFileSystem as IDisposable)?.Dispose();
                    _stream.Dispose();
                    _disposed = true;
                }
            }
            
            // 实现 IFileSystem 接口的所有方法，将它们委托给 _baseFileSystem
            public Result CreateDirectory(ref readonly Path path)
            {
                return _baseFileSystem.CreateDirectory(in path);
            }
            
            public Result CreateFile(ref readonly Path path, long size, CreateFileOptions options)
            {
                return _baseFileSystem.CreateFile(in path, size, options);
            }
            
            public Result DeleteDirectory(ref readonly Path path)
            {
                return _baseFileSystem.DeleteDirectory(in path);
            }
            
            public Result DeleteDirectoryRecursively(ref readonly Path path)
            {
                return _baseFileSystem.DeleteDirectoryRecursively(in path);
            }
            
            public Result DeleteFile(ref readonly Path path)
            {
                return _baseFileSystem.DeleteFile(in path);
            }
            
            public Result CleanDirectoryRecursively(ref readonly Path path)
            {
                return _baseFileSystem.CleanDirectoryRecursively(in path);
            }
            
            public Result GetEntryType(out DirectoryEntryType entryType, ref readonly Path path)
            {
                return _baseFileSystem.GetEntryType(out entryType, in path);
            }
            
            public Result OpenFile(ref UniqueRef<IFile> outFile, ref readonly Path path, OpenMode mode)
            {
                return _baseFileSystem.OpenFile(ref outFile, in path, mode);
            }
            
            public Result OpenDirectory(ref UniqueRef<IDirectory> outDirectory, ref readonly Path path, OpenDirectoryMode mode)
            {
                return _baseFileSystem.OpenDirectory(ref outDirectory, in path, mode);
            }
            
            public Result RenameFile(ref readonly Path currentPath, ref readonly Path newPath)
            {
                return _baseFileSystem.RenameFile(in currentPath, in newPath);
            }
            
            public Result RenameDirectory(ref readonly Path currentPath, ref readonly Path newPath)
            {
                return _baseFileSystem.RenameDirectory(in currentPath, in newPath);
            }
            
            public Result GetFileTimeStampRaw(out FileTimeStampRaw timeStamp, ref readonly Path path)
            {
                return _baseFileSystem.GetFileTimeStampRaw(out timeStamp, in path);
            }
            
            public Result QueryEntry(Span<byte> outBuffer, Span<byte> inBuffer, QueryId queryId, ref readonly Path path)
            {
                return _baseFileSystem.QueryEntry(outBuffer, inBuffer, queryId, in path);
            }
            
            public Result Commit()
            {
                return _baseFileSystem.Commit();
            }
            
            public Result GetFreeSpaceSize(out long freeSpace, ref readonly Path path)
            {
                return _baseFileSystem.GetFreeSpaceSize(out freeSpace, in path);
            }
            
            public Result GetTotalSpaceSize(out long totalSpace, ref readonly Path path)
            {
                return _baseFileSystem.GetTotalSpaceSize(out totalSpace, in path);
            }
            
            public Result GetFileSystemAttribute(out FileSystemAttribute outAttribute)
            {
                return _baseFileSystem.GetFileSystemAttribute(out outAttribute);
            }
        }
    }
}
