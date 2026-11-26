using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private readonly SemaphoreSlim captureGate=new SemaphoreSlim(1);
        private CaptureInbox captureInbox;
        private const string TakePicture="{AF933CAC-ACAD-11D2-A093-00C04F72DC3C}";

        private async Task<object> CaptureAction(string method,Dictionary<string,object> args)
        {
            if(!await captureGate.WaitAsync(0))throw new InvalidOperationException("拍摄导入正在工作，请稍后重试。");
            try
            {
                if(method=="captureWatch")
                {
                    using(var dialog=new FolderBrowserDialog{Description=L("选择相机软件写入新照片的目录","Choose the folder where your camera software saves photos"),ShowNewFolderButton=false})
                        if(dialog.ShowDialog(owner)==DialogResult.OK)
                        {
                            string dir=dialog.SelectedPath;
                            captureInbox=await Task.Run(()=>new CaptureInbox(dir,extensions));
                        }
                }
                else if(method=="captureStop")captureInbox=null;
                else if(method=="captureCameras")return await StaCamera(()=>WiaCameras(null,null));
                else if(method=="captureShutter")
                {
                    string device=TextOf(args,"deviceId");
                    if(String.IsNullOrEmpty(device)||device.Length>2048)throw new ArgumentException("请选择一台已连接的相机。");
                    string dir=captureInbox==null?null:captureInbox.DirectoryPath;
                    if(dir==null)using(var dialog=new FolderBrowserDialog{Description=L("选择新拍照片的保存目录","Choose where to save new captures")})
                    {
                        if(dialog.ShowDialog(owner)!=DialogResult.OK)return new {cancelled=true};
                        dir=dialog.SelectedPath;
                    }
                    string file=(string)await StaCamera(()=>WiaCameras(device,dir));
                    try {await Task.Run(()=>work.ImportOriginal(file,false));}
                    catch(Exception e){throw new IOException("相机照片已保存到 "+file+"。图库导入失败："+e.GetBaseException().Message,e);}
                    if(captureInbox!=null)captureInbox.RegisterCapture(file);
                    return new {file=file};
                }
                int added=0;
                if(method=="capturePoll"&&captureInbox!=null)
                    added=await Task.Run(()=>captureInbox.Read(path=>work.ImportOriginal(path,false),closing.Token));
                var inbox=captureInbox;
                return new {folder=inbox==null?"":inbox.DirectoryPath,added=added,imported=inbox==null?0:inbox.Imported,
                    failed=inbox==null?0:inbox.Failed,lastFile=inbox==null?"":inbox.LastFile,lastError=inbox==null?"":inbox.LastError};
            }
            finally{captureGate.Release();}
        }
        private static Task<object> StaCamera(Func<object> action)
        {
            var result=new TaskCompletionSource<object>();
            var worker=new Thread(delegate() {
                try{result.SetResult(action());}
                catch(Exception error){result.SetException(new InvalidOperationException("Windows 相机接口："+error.GetBaseException().Message,error));}
            }){IsBackground=true};
            worker.SetApartmentState(ApartmentState.STA);worker.Start();
            return result.Task;
        }
        private static object WiaValue(object item,string key)
        {
            return item.GetType().InvokeMember(key,BindingFlags.GetProperty,null,item,null);
        }
        private static object WiaCall(object item,string name,params object[] arguments)
        {
            return item.GetType().InvokeMember(name,BindingFlags.InvokeMethod|BindingFlags.OptionalParamBinding,null,item,arguments);
        }
        private object WiaCameras(string chosen,string folder)
        {
            Type type=Type.GetTypeFromProgID("WIA.DeviceManager");
            if(type==null)throw new InvalidOperationException("这台电脑未提供 WIA 相机接口，可使用拍摄目录。");
            object manager=Activator.CreateInstance(type);
            try {return CameraDevices((IEnumerable)WiaValue(manager,"DeviceInfos"),chosen,folder);}
            finally{Marshal.ReleaseComObject(manager);}
        }
        internal object CameraDevices(IEnumerable devices,string chosen,string folder)
        {
            var list=new List<object>();
            foreach(object info in devices)
            {
                if(Convert.ToInt32(WiaValue(info,"Type"))!=2)continue;
                string id=Convert.ToString(WiaValue(info,"DeviceID"));
                if(chosen!=null&&!String.Equals(chosen,id,StringComparison.Ordinal))continue;
                string name="WIA 相机";object device=null;
                try
                {
                    foreach(object prop in (IEnumerable)WiaValue(info,"Properties"))
                        if(String.Equals(Convert.ToString(WiaValue(prop,"Name")),"Name",StringComparison.OrdinalIgnoreCase))
                            name=Convert.ToString(WiaValue(prop,"Value"));
                    device=WiaCall(info,"Connect");
                    bool shutter=false;
                    foreach(object command in (IEnumerable)WiaValue(device,"Commands"))
                        if(String.Equals(Convert.ToString(WiaValue(command,"CommandID")),TakePicture,StringComparison.OrdinalIgnoreCase))shutter=true;
                    if(chosen==null){list.Add(new {id=id,name=name,shutter=shutter});continue;}
                    if(!shutter)throw new InvalidOperationException("此设备未提供拍摄指令，可使用相机软件的拍摄目录。");
                    return SaveCameraFile(device,folder);
                }
                catch(Exception ex)
                {
                    if(chosen!=null||!(ex.GetBaseException() is COMException))throw;
                    list.Add(new {id=id,name=name,shutter=false,issue=ex.GetBaseException().Message});
                }
                finally{ReleaseCameraObject(device);}
            }
            if(chosen!=null)throw new InvalidOperationException("没有找到这台相机，请重新连接后刷新。");
            return list;
        }
        private static void ReleaseCameraObject(object item)
        {
            if(item!=null&&Marshal.IsComObject(item))Marshal.ReleaseComObject(item);
        }
        private string SaveCameraFile(object device,string folder)
        {
            object item=WiaCall(device,"ExecuteCommand",TakePicture);
            if(item==null)throw new IOException("已发送快门指令，设备未返回照片，请先查看相机。");
            try
            {
                object photo=WiaCall(item,"Transfer",Guid.Empty.ToString("B"));
                if(photo==null)throw new IOException("相机没有传回照片，请检查连接与拍摄目录。");
                try
                {
                    string ext=Convert.ToString(WiaValue(photo,"FileExtension")).TrimStart('.').ToLowerInvariant();
                    if(!extensions.Contains("."+ext))
                        throw new InvalidDataException("设备返回的格式暂不支持，请从相机导入原片。");
                    string stem="拍摄-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N");
                    string path=Path.Combine(folder,stem+"."+ext),temp=Path.Combine(folder,stem+".partial");
                    WiaCall(photo,"SaveFile",temp);
                    try{File.Move(temp,path);return path;}
                    catch{throw new IOException("新照片已保存在 "+temp+"，未能完成更名。");}
                }
                finally{ReleaseCameraObject(photo);}
            }
            finally{ReleaseCameraObject(item);}
        }
    }
}
