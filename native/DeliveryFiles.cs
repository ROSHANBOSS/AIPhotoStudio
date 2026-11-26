using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AIPhotoStudio
{
    internal sealed class SharedDeliveryPause : IOException
    {
        internal SharedDeliveryPause(string reason, Exception cause=null) : base(reason,cause) {}
        internal static bool IsShare(string root){return root.StartsWith(@"\\",StringComparison.Ordinal);}

        internal static string ResolveRoot(string directory)
        {
            string path=WorkspacePaths.Full(directory);
            if(IsShare(path))return path;
            var drive=new DriveInfo(Path.GetPathRoot(path));
            var type=drive.DriveType;
            if(type!=DriveType.Network && type!=DriveType.NoRootDirectory && type!=DriveType.Unknown)return path;

            string letter=path.Substring(0,2);
            int size=512;
            var remote=new StringBuilder(size);
            int status=WNetGetConnectionW(letter,remote,ref size);
            if(status==234 && size>remote.Capacity && size<=32768)
            {
                remote=new StringBuilder(size);
                status=WNetGetConnectionW(letter,remote,ref size);
            }
            return MappedRoot(path,status,remote.ToString());
        }

        internal static string MappedRoot(string path,int status,string remote)
        {
            if(status==0 && IsShare(remote))
                return WorkspacePaths.Full(remote.TrimEnd('\\')+path.Substring(2));
            throw new SharedDeliveryPause("暂时无法确认交付盘 "+path.Substring(0,2)+" 的位置。请重新连接原盘或恢复映射后再试。");
        }

        [DllImport("mpr.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
        private static extern int WNetGetConnectionW(string localName,StringBuilder remoteName,ref int size);

        internal static bool LostConnection(string root,Exception error)
        {
            for(var cause=error;cause!=null;cause=cause.InnerException)
            {
                var os=cause as Win32Exception;
                uint hr=unchecked((uint)cause.HResult);
                if(os==null && (hr>>16)!=0x8007)continue;
                int code=os!=null?os.NativeErrorCode:(int)(hr&65535);
                switch(code)
                {
                    case 53: case 54: case 58: case 59: case 64: case 67: case 1201: case 1203:
                    case 1222: case 1225: case 1226: case 1231: case 1232: case 1236: case 2250:
                        return true;
                    case 55: case 121:
                        if(IsShare(root))return true;
                        break;
                }
            }
            return false;
        }
    }

    // Hold the selected root and its immediate child without delete sharing during publication.
    internal sealed class DeliveryDirectory : IDisposable
    {
        private SafeFileHandle root,child;
        internal readonly string PathName;

        internal static string RootIdentity(string directory)
        {
            using(var handle=Pin(directory))
            {
                string identity=Identity(handle);
                if(identity==null)throw new IOException("所选目录没有提供可核对的文件身份。请改选普通本地文件夹或支持文件身份的共享目录，重新开始双份交付。");
                return identity;
            }
        }

        internal static SafeFileHandle HoldRoot(string directory,string expectedRoot)
        {
            if(String.IsNullOrEmpty(expectedRoot))throw new SharedDeliveryPause("这批记录缺少原交付目录的文件身份。请在批量处理中重新开始双份交付，选择可核对的目录。");
            try
            {
                var handle=Pin(directory);
                try
                {
                    if(Identity(handle,expectedRoot)!=expectedRoot)throw new SharedDeliveryPause("原交付目录已被替换或无法核对身份。请恢复原目录，或选择新目录重新交付。");
                    return handle;
                }
                catch{handle.Dispose();throw;}
            }
            catch(SharedDeliveryPause){throw;}
            catch(IOException error)
            {
                throw new SharedDeliveryPause("暂时无法打开原交付目录。请恢复原文件夹、连接及访问权限后继续补交。",error);
            }
        }

        internal DeliveryDirectory(string directory,string expectedRoot,string folder,string otherFolder)
        {
            root=HoldRoot(directory,expectedRoot);
            try
            {
                PathName=Path.Combine(directory,folder);
                Directory.CreateDirectory(PathName);
                child=Pin(PathName);
                string other=Path.Combine(directory,otherFolder);
                if((folder.Contains("~")||otherFolder.Contains("~")) && Directory.Exists(other))using(var handle=Open(other,0x80,3,0x02000000|0x00200000)){
                    string identity=Identity(child);
                    string otherIdentity=Identity(handle,identity);
                    if(identity==null || otherIdentity==null)throw new IOException("无法区分两个交付子目录。请在预设中使用两个不含波浪号的普通文件夹名称。");
                    if(identity==otherIdentity)throw new IOException("小样和成片的目录名称指向了同一文件夹。");
                }
            }
            catch{Dispose();throw;}
        }

        private static SafeFileHandle Pin(string path)
        {
            var handle=Open(path,0x80,3,0x02000000|0x00200000);
            try
            {
                var attributes=(FileAttributes)Info(handle).attributes;
                if((attributes&FileAttributes.Directory)==0)throw new IOException("交付目录的位置现在是文件，请恢复原文件夹。");
                if((attributes&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("交付目录不能指向目录联接或符号链接，请使用普通文件夹。");
                return handle;
            }
            catch{handle.Dispose();throw;}
        }

        internal static string Digest(Stream input,CancellationToken cancel)
        {
            input.Position=0;var bytes=new byte[65536];int count;
            using(var sha=SHA256.Create())
            {
                while((count=input.Read(bytes,0,bytes.Length))>0){cancel.ThrowIfCancellationRequested();sha.TransformBlock(bytes,0,count,null,0);}
                sha.TransformFinalBlock(bytes,0,0);return Convert.ToBase64String(sha.Hash);
            }
        }

        internal static bool Matches(string path,string digest,string stagedIdentity,CancellationToken cancel)
        {
            using(var handle=Open(path,0x80000000,1,0x02000000|0x00200000,true))
            {
                if(handle==null)return false;
                if(((FileAttributes)Info(handle).attributes&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)return false;
                if(stagedIdentity!=null && Identity(handle,stagedIdentity)!=stagedIdentity)return false;
                using(var stream=new FileStream(handle,FileAccess.Read))return Digest(stream,cancel)==digest;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInfoNative
        {
            internal uint attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME created,accessed,written;
            internal uint volume,highSize,lowSize,links,highIndex,lowIndex;
        }
        private static FileInfoNative Info(SafeFileHandle handle)
        {
            FileInfoNative info;
            if(!GetFileInformationByHandle(handle,out info))throw new IOException("无法读取交付文件的身份。",new Win32Exception(Marshal.GetLastWin32Error()));
            return info;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInfo {internal ulong volume,first,second;}

        internal static string Identity(SafeFileHandle handle,string expected=null)
        {
            var f=Info(handle);
            string created=f.created.dwHighDateTime.ToString("x8")+f.created.dwLowDateTime.ToString("x8");
            // Keep the 64-bit identity already stored by earlier versions whenever the provider supplies one.
            if((f.highIndex!=0 || f.lowIndex!=0) && (expected==null || !expected.StartsWith("id128/",StringComparison.Ordinal)))
                return f.volume.ToString("x8")+"/"+f.highIndex.ToString("x8")+f.lowIndex.ToString("x8")+"/"+created;
            FileIdInfo id;
            if(GetFileInformationByHandleEx(handle,18,out id,(uint)Marshal.SizeOf(typeof(FileIdInfo))))
                return (id.first|id.second)==0?null:"id128/"+id.volume.ToString("x16")+"/"+id.first.ToString("x16")+id.second.ToString("x16")+"/"+created;
            int error=Marshal.GetLastWin32Error();
            if(error==1 || error==50 || error==87 || error==120)return null;
            throw new IOException("无法核对交付位置的文件身份。",new Win32Exception(error));
        }

        internal void RecoverStage(DeliveryFile file)
        {
            // Only our recorded file identity may be removed. Another file can legitimately reuse the path after a crash.
            if(String.IsNullOrEmpty(file.temporary) || String.IsNullOrEmpty(file.identity))return;
            Guid token;
            if(!file.temporary.StartsWith(".aips-",StringComparison.Ordinal) || !file.temporary.EndsWith(".tmp",StringComparison.Ordinal) ||
                file.temporary.Length!=42 || !Guid.TryParseExact(file.temporary.Substring(6,32),"N",out token))return;
            string path=Path.Combine(PathName,file.temporary);
            using(var handle=Open(path,0x00010000|0x80,0,0x00200000,true))
            {
                if(handle==null)return;
                if(Identity(handle,file.identity)!=file.identity)return;
                byte delete=1;
                if(!SetFileInformationByHandle(handle,4,ref delete,1))throw new IOException("无法清理上次中断的交付临时文件。",new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }

        private static SafeFileHandle Open(string path,uint access,uint sharing,uint flags,bool allowMissingFile=false)
        {
            var handle=CreateFileW(WorkspacePaths.Extended(WorkspacePaths.Full(path)),access,sharing,IntPtr.Zero,3,flags,IntPtr.Zero);
            if(!handle.IsInvalid)return handle;
            int code=Marshal.GetLastWin32Error();handle.Dispose();
            if(allowMissingFile && code==2)return null;
            throw new IOException("无法打开交付位置 "+path,new Win32Exception(code));
        }

        public void Dispose(){if(child!=null)child.Dispose();if(root!=null)root.Dispose();}

        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint create,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfoNative info);
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out FileIdInfo info,uint size);
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref byte info,uint size);
    }
}
