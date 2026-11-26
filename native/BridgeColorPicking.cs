using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private sealed class ColorInputTicket
        {
            internal string Id;
            internal long Revision,Modified,Created,Length;
            internal Task<Imaging.ColorBandInput> Read;
        }
        private ColorInputTicket bandTicket;
        private async Task<Imaging.ColorBandInput> ReadColorInput(string path,Dictionary<string,double> values)
        {
            await renderSlots.WaitAsync(closing.Token);
            try{return await Task.Run(()=>new Imaging.ColorBandInput(path,values,closing.Token),closing.Token);}
            finally{renderSlots.Release();}
        }
        private async Task<object> InspectColorBand(string method,Dictionary<string,object> args)
        {
            string id=TextOf(args,"id"),path=work.PathFor(id);
            long revision=Convert.ToInt64(args["revision"]);
            work.VerifyPreviewRevision(id,revision);
            var file=new FileInfo(path);
            var ticket=bandTicket;
            if(ticket==null||ticket.Id!=id||ticket.Revision!=revision||ticket.Modified!=file.LastWriteTimeUtc.Ticks||
                ticket.Created!=file.CreationTimeUtc.Ticks||ticket.Length!=file.Length)
            {
                ticket=new ColorInputTicket{Id=id,Revision=revision,Modified=file.LastWriteTimeUtc.Ticks,
                    Created=file.CreationTimeUtc.Ticks,Length=file.Length,Read=ReadColorInput(path,work.PreviewValues(id,revision,null,false))};
                bandTicket=ticket;
            }
            Imaging.ColorBandInput input;
            try{input=await ticket.Read;}
            catch{if(bandTicket==ticket)bandTicket=null;throw;}
            bool composed=TextOf(args,"frameView")=="output";
            object reply=await Task.Run<object>(delegate{
                var token=closing.Token;
                if(method=="colorBandInput")return EncodeFrame(input.View(input.Source,composed,token),false,token);
                int x=Coordinate(args["x"],input.Source.PixelWidth),y=Coordinate(args["y"],input.Source.PixelHeight);
                string band;double strength;
                var overlay=input.Sample(x,y,composed,token,out band,out strength);
                return new {band=band,strength=strength,overlay=overlay==null?null:EncodeFrame(overlay,false,token)["image"]};
            },closing.Token);
            work.VerifyPreviewRevision(id,revision);
            return reply;
        }

        private async Task<object> SampleWhiteCandidate(Dictionary<string,object> args)
        {
            string id=TextOf(args,"id");
            long revision=Convert.ToInt64(args["revision"]);
            var doc=Selected(args);
            var values=Json().ConvertToType<Dictionary<string,double>>(doc["parameters"]);
            var view=TextOf(args,"frameView")=="source"?FrameGeometry.SourceValues(values):values;
            var picture=await Render(work.PathFor(id),view,PreviewEdge(args,false));
            int x=Coordinate(args["x"],picture.PixelWidth),y=Coordinate(args["y"],picture.PixelHeight);
            var proposed=Imaging.PickWhiteBalance(picture,x,y,values);
            var locks=new HashSet<string>(Strings(doc["locks"]));
            var patch=proposed.Where(p=>!locks.Contains(p.Key)&&p.Value!=values[p.Key]).ToDictionary(p=>p.Key,p=>p.Value);
            int left=Math.Max(0,x-9),top=Math.Max(0,y-9);
            int width=Math.Min(picture.PixelWidth,x+10)-left,height=Math.Min(picture.PixelHeight,y+10)-top;
            var detail=new CroppedBitmap(picture,new Int32Rect(left,top,width,height));
            detail.Freeze();
            var encoded=(Dictionary<string,object>)await EncodePreview(detail);
            work.VerifyPreviewRevision(id,revision);
            return new {values=patch,loupe=encoded["image"],
                sampleBox=new {x=(double)Math.Max(0,x-left-2)/width,y=(double)Math.Max(0,y-top-2)/height,
                    w=(double)(Math.Min(picture.PixelWidth,x+3)-Math.Max(0,x-2))/width,
                    h=(double)(Math.Min(picture.PixelHeight,y+3)-Math.Max(0,y-2))/height}};
        }
    }
}
