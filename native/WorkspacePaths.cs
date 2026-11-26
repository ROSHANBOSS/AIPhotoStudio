using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AIPhotoStudio
{
    internal static class WorkspacePaths
    {
        internal static string Full(string path)
        {
            if(String.IsNullOrWhiteSpace(path) || path.IndexOf('\0')>=0) throw new ArgumentException("本地路径为空或无效。");
            path=path.Replace('/','\\');
            if(path.StartsWith(@"\\.\",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请输入文件或文件夹路径。");
            if(path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase)) path=@"\\"+path.Substring(8);
            else if(path.StartsWith(@"\\?\",StringComparison.OrdinalIgnoreCase))
            {
                path=path.Substring(4);
                if(path.Length<3 || path[1]!=':' || path[2]!='\\') throw new ArgumentException("只接受本地磁盘或 UNC 文件路径。");
            }
            var buffer=new StringBuilder(Math.Max(512,path.Length+2));
            uint count=GetFullPathNameW(path,(uint)buffer.Capacity,buffer,IntPtr.Zero);
            if(count>=buffer.Capacity)
            {
                buffer=new StringBuilder(checked((int)count+1));
                count=GetFullPathNameW(path,(uint)buffer.Capacity,buffer,IntPtr.Zero);
            }
            if(count==0 || count>=buffer.Capacity) throw new IOException("无法解析本地路径。",new Win32Exception(Marshal.GetLastWin32Error()));
            return buffer.ToString();
        }

        internal static string Extended(string fullPath)
        {
            if(fullPath.StartsWith(@"\\?\",StringComparison.OrdinalIgnoreCase)) return fullPath;
            return fullPath.StartsWith(@"\\",StringComparison.Ordinal) ? @"\\?\UNC\"+fullPath.Substring(2) : @"\\?\"+fullPath;
        }
        internal static string ForIO(string fullPath) { return fullPath.Length<248 ? fullPath : Extended(fullPath); }

        internal static string DirectoryOf(string fullPath)
        {
            int slash=fullPath.LastIndexOf('\\');
            if(slash<0) throw new ArgumentException("需要完整的文件路径。");
            return slash==2 && fullPath[1]==':' ? fullPath.Substring(0,3) : fullPath.Substring(0,slash);
        }

        internal static FileStream OpenRead(string fullPath,FileShare sharing)
        {
            var handle=CreateFileW(Extended(fullPath),0x80000000,(uint)sharing,IntPtr.Zero,3,0,IntPtr.Zero);
            if(handle.IsInvalid)
            {
                int code=Marshal.GetLastWin32Error(); handle.Dispose();
                if(code==2 || code==3) throw new FileNotFoundException("找不到原片。",fullPath);
                throw new IOException("无法只读打开原片。",new Win32Exception(code));
            }
            try { return new FileStream(handle,FileAccess.Read,65536,false); }
            catch { handle.Dispose(); throw; }
        }

        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        private static extern uint GetFullPathNameW(string path,uint size,StringBuilder full,IntPtr filePart);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    }
}
