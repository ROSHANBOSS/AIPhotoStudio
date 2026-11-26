using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private Task<int> originalIndex;
        private CancellationTokenSource folderScan;

        private Task<int> StartOriginalIndex(bool repeat)
        {
            if (originalIndex==null || (repeat && originalIndex.IsCompleted))
                originalIndex=Task.Run(()=>work.BackfillOriginals(closing.Token),closing.Token);
            return originalIndex;
        }

        private async Task<object> RelinkAction(string method, Dictionary<string,object> args)
        {
            if (method=="relinkPrepare")
            {
                await StartOriginalIndex(true);
                return work.OriginalCoverage();
            }
            if (method=="relinkCancel")
            {
                if (folderScan!=null) folderScan.Cancel();
                return null;
            }
            if (method=="relinkScan")
            {
                using (var picker=new FolderBrowserDialog { Description=L("选择搬移后的原片文件夹（含子文件夹）","Choose the folder containing the relocated originals, including subfolders"), ShowNewFolderButton=false })
                {
                    if (picker.ShowDialog(owner)!=DialogResult.OK) return null;
                    var scan=CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
                    if (folderScan!=null) folderScan.Cancel();
                    folderScan=scan;
                    string folder=picker.SelectedPath;
                    try { return await Task.Run(()=>work.ScanRelink(folder,extensions,scan.Token)); }
                    catch (OperationCanceledException) { return null; }
                    finally { if (folderScan==scan) folderScan=null; scan.Dispose(); }
                }
            }
            string token=TextOf(args,"token");
            if (method=="relinkApply")
            {
                var choices=StudioWorkspace.ReadRelinkChoices(args);
                int changed=await Task.Run(()=>work.ApplyRelink(token,choices,closing.Token));
                var state=Snapshot();
                state["relinkCount"]=changed;
                return state;
            }
            int row=RelinkIndex(args,"row");
            if (method=="relinkPick")
            {
                using (var picker=new OpenFileDialog { Title=L("选择要与这条旧记录核对的原片","Choose the original to compare with this library entry"), CheckFileExists=true,
                    Filter=L("支持的照片|","Supported photos|")+String.Join(";",extensions.Select(e=>"*"+e)) })
                {
                    if (picker.ShowDialog(owner)!=DialogResult.OK) return null;
                    string path=picker.FileName;
                    if (!extensions.Contains(Path.GetExtension(path))) throw new ArgumentException("请选择支持的照片格式。");
                    return await Task.Run(()=>work.PickRelinkFile(token,row,path));
                }
            }
            int candidate=RelinkIndex(args,"candidate");
            return await Task.Run(()=>work.ViewRelinkFile(token,row,candidate,path=>
                EncodeFrame(Imaging.Render(path,new Dictionary<string,double>(),320,closing.Token),true,closing.Token)));
        }
        private static int RelinkIndex(Dictionary<string,object> args, string key)
        {
            object value;
            if (!args.TryGetValue(key,out value)) throw new ArgumentException("缺少候选编号。");
            double number=StudioWorkspace.Number(value);
            if (number<0 || number>Int32.MaxValue || number!=Math.Truncate(number)) throw new ArgumentException("候选编号无效。");
            return (int)number;
        }
    }

}
