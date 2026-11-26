using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

internal static class Setup
{
#if DEV_SETUP
    private const string ChannelDirectory = "Development";
    private const string ChannelName = "AIPhotoStudio 开发版";
#else
    private const string ChannelDirectory = "Stable";
    private const string ChannelName = "AIPhotoStudio 0.5";
#endif
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        bool testing = args.Length==2 && args[0]=="--verify-install-to" && Path.IsPathRooted(args[1]);
        bool extract = args.Length==2 && args[0]=="--extract-to" && Path.IsPathRooted(args[1]);
        if(args.Length!=0&&!extract&&!testing)return 2;
        try
        {
            string root=extract?Path.GetFullPath(args[1]):Path.Combine(testing?Path.GetFullPath(args[1]):Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","AIPhotoStudio",ChannelDirectory);
            if(extract&&Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new IOException("验收目标目录须为空。");
            if(!testing&&!extract&&MessageBox.Show("将为当前用户安装 "+ChannelName+"，并创建开始菜单快捷方式。\n\n程序位置："+root+"\n原片、图库与预设独立保存。","安装 "+ChannelName,MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK)return 0;
            Directory.CreateDirectory(root);
            string staging=Path.Combine(root,".setup-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            string target;
            try
            {
                using(var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("application.zip"))
                {
                    string digest;using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(payload)).Replace("-","").Substring(0,16);
                    target=Path.Combine(root,Application.ProductVersion+"-"+digest);
                    payload.Position=0;
                    using(var zip=new ZipArchive(payload,ZipArchiveMode.Read))
                    {
                        string prefix=Path.GetFullPath(staging)+Path.DirectorySeparatorChar;
                        foreach(var item in zip.Entries)
                        {
                            string dest=Path.GetFullPath(Path.Combine(staging,item.FullName));
                            if(!dest.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("无效的安装包条目。");
                            if(String.IsNullOrEmpty(item.Name))continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            using(var output=new FileStream(dest,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                            using(var input=item.Open())input.CopyTo(output);
                        }
                    }
                }
                if(!File.Exists(Path.Combine(staging,"AIPhotoStudio.exe")))throw new InvalidDataException("程序入口缺失。");
                if(!Directory.Exists(target))Directory.Move(staging,target);
                else if(!Identical(staging,target))throw new IOException("已有安装中的文件与此包不符。请保留现有目录并改用独立位置。");
            }
            finally { if(Directory.Exists(staging))Directory.Delete(staging,true); } // unique directory created above
            if(!extract)
            {
                string menu=Path.Combine(testing?Path.Combine(Path.GetFullPath(args[1]),"StartMenu"):Environment.GetFolderPath(Environment.SpecialFolder.Programs),"AIPhotoStudio");
                Directory.CreateDirectory(menu);
                Type shellType=Type.GetTypeFromProgID("WScript.Shell",true);
                object shell=Activator.CreateInstance(shellType);
                object shortcut=shellType.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{Path.Combine(menu,ChannelName+".lnk")});
                var flags=BindingFlags.SetProperty;
                shortcut.GetType().InvokeMember("TargetPath",flags,null,shortcut,new object[]{Path.Combine(target,"AIPhotoStudio.exe")});
                shortcut.GetType().InvokeMember("WorkingDirectory",flags,null,shortcut,new object[]{target});
                shortcut.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,shortcut,null);
                if(!testing)MessageBox.Show("安装完成。可在开始菜单中打开 "+ChannelName+"。",ChannelName);
            }
            return 0;
        }
        catch(Exception e){if(extract||testing)Console.Error.WriteLine(e.GetBaseException().Message);else MessageBox.Show(e.GetBaseException().Message,"安装未完成");return 1;}
    }

    private static bool Identical(string source,string destination)
    {
        var files=Directory.GetFiles(source,"*",SearchOption.AllDirectories);
        if(files.Length!=Directory.GetFiles(destination,"*",SearchOption.AllDirectories).Length)return false;
        foreach(string name in files)
        {
            string other=Path.Combine(destination,name.Substring(source.Length+1));
            if(!File.Exists(other))return false;
            using(var a=File.OpenRead(name))using(var b=File.OpenRead(other))using(var sha=SHA256.Create())
                if(!sha.ComputeHash(a).SequenceEqual(sha.ComputeHash(b)))return false;
        }
        return true;
    }
}
