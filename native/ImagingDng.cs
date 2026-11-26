using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed partial class RawInspection
    {
        // The camera transform runs before this fixed exposure reserve is encoded into the 16-bit preview cache.
        // Render restores the reserve in linear light together with the user's EV, before highlight clipping.
        internal const double DngReserveEv = 3;
        internal sealed class DngLayout
        {
            internal int W, H, Left, Top, Right, Bottom, X, Y, Width, Height, Orientation;
            internal int[] Pattern;
            internal double[] Black, InverseRange, Color;
        }

        private bool Has(int tag) { return sensor.ContainsKey(tag) || firstDirectory.ContainsKey(tag); }
        private double[] Tag(Stream stream, int tag, int limit)
        {
            return Numbers(stream, sensor.ContainsKey(tag) ? sensor : firstDirectory, tag, limit);
        }
        private double[] TagOr(Stream stream, int tag, params double[] defaults)
        {
            if (!Has(tag)) return defaults;
            return Tag(stream, tag, Math.Max(defaults.Length, 16));
        }
        private static NotSupportedException DngLimit(string reason)
        {
            return new NotSupportedException("该 DNG 不符合本机已验证的传感器处理条件：" + reason + "。");
        }
        private static int[] Integers(double[] numbers, int count, string label)
        {
            if (numbers.Length != count) throw DngLimit(label);
            int[] result = new int[count];
            for (int i = 0; i < count; i++)
            {
                if (numbers[i] != Math.Truncate(numbers[i]) || numbers[i] < 0 || numbers[i] > 100000) throw DngLimit(label);
                result[i] = (int)numbers[i];
            }
            return result;
        }
        private static bool Same(double[] a, params double[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > .000001) return false;
            return true;
        }
        private static double[] Invert(double[] m)
        {
            double[] v = {
                m[4]*m[8]-m[5]*m[7], m[2]*m[7]-m[1]*m[8], m[1]*m[5]-m[2]*m[4],
                m[5]*m[6]-m[3]*m[8], m[0]*m[8]-m[2]*m[6], m[2]*m[3]-m[0]*m[5],
                m[3]*m[7]-m[4]*m[6], m[1]*m[6]-m[0]*m[7], m[0]*m[4]-m[1]*m[3] };
            double determinant = m[0]*v[0] + m[1]*v[3] + m[2]*v[6];
            if (Math.Abs(determinant) < .0000001) throw DngLimit("色彩矩阵不可逆");
            for (int i = 0; i < 9; i++) v[i] /= determinant;
            return v;
        }
        private static double[] Compose(double[] a, double[] b)
        {
            double[] result = new double[9];
            for (int row=0; row<3; row++) for (int col=0; col<3; col++)
                for (int k=0; k<3; k++) result[row*3+col] += a[row*3+k]*b[k*3+col];
            return result;
        }
        private static double[] Transform(double[] matrix, double[] vector)
        {
            return new[] {matrix[0]*vector[0]+matrix[1]*vector[1]+matrix[2]*vector[2],
                matrix[3]*vector[0]+matrix[4]*vector[1]+matrix[5]*vector[2],
                matrix[6]*vector[0]+matrix[7]*vector[1]+matrix[8]*vector[2]};
        }
        private double[] CameraTransform(Stream stream)
        {
            if (!Same(Tag(stream,50778,1),21) || Has(50722) || Has(50779) || Has(50964) || Has(50965))
                throw DngLimit("需要单一 D65 校准光源与 ColorMatrix1");
            double[] identity = {1,0,0,0,1,0,0,0,1};
            if (!Same(TagOr(stream,50723,identity),identity) || !Same(TagOr(stream,50727,1,1,1),1,1,1))
                throw DngLimit("相机校准或模拟增益需要额外处理");
            double[] color = Tag(stream,50721,9), neutral = Tag(stream,50728,3);
            if (color.Length != 9 || neutral.Length != 3) throw DngLimit("缺少三通道相机色彩矩阵或拍摄白平衡");
            for (int i = 0; i < 9; i++) if (Math.Abs(color[i]) > 16) throw DngLimit("相机色彩矩阵超出范围");
            double[] inverse = Invert(color);
            double[] xyzToSrgb = {3.2404542,-1.5371385,-.4985314, -.9692660,1.8760108,.0415560, .0556434,-.2040259,1.0572252};
            double[] white = {.95047,1,1.08883};
            double peakNeutral = Math.Max(neutral[0], Math.Max(neutral[1], neutral[2]));
            double[] exposure = TagOr(stream,50730,0);
            if (exposure.Length != 1 || Math.Abs(exposure[0]) > 5 || peakNeutral <= 0) throw DngLimit("基线曝光或白平衡无效");
            double reserve = Math.Pow(2, exposure[0] - DngReserveEv);
            for (int c = 0; c < 3; c++)
            {
                if (neutral[c] <= 0 || neutral[c]/peakNeutral < .04) throw DngLimit("白平衡比例不受支持");
                neutral[c] /= peakNeutral;
            }
            // An inverse ColorMatrix maps the shot neutral to XYZ. Adapt that measured white to the output D65 white.
            double[] bradford = {.8951,.2664,-.1614, -.7502,1.7135,.0367, .0389,-.0685,1.0296};
            double[] sourceCone=Transform(bradford,Transform(inverse,neutral)), targetCone=Transform(bradford,white), diagonal=new double[9];
            for (int c=0; c<3; c++)
            {
                if (sourceCone[c] <= .00001) throw DngLimit("拍摄白点不在可适应的色度范围内");
                diagonal[c*4]=targetCone[c]/sourceCone[c];
            }
            double[] result=Compose(xyzToSrgb,Compose(Invert(bradford),Compose(diagonal,Compose(bradford,inverse))));
            for (int row = 0; row < 3; row++)
            {
                double maxSignal = 0;
                for (int col = 0; col < 3; col++)
                {
                    result[row*3+col] *= reserve;
                    maxSignal += Math.Max(result[row*3+col], 0);
                }
                if (maxSignal > 1.0001) throw DngLimit("色彩变换超出三档高光余量");
            }
            return result;
        }

        internal bool UsesLosslessCameraDng(Stream stream)
        {
            if(sensor==null||!Properties.ContainsKey("dngVersion")||(long)Properties["compressionCode"]!=7)return false;
            if(sensorDirectories!=1)throw DngLimit("此无损 JPEG DNG 含多个主感光目录，当前不能选择其中的帧");
            if((long)Properties["photometric"]!=32803)throw DngLimit("这份线性 DNG 已去马赛克，当前可解码相机的 Bayer CFA DNG");
            if(Has(51008)||Has(51009)||Has(51022))throw DngLimit("文件含当前解码器尚未实现的 DNG Opcode 校正");
            if(!Same(Tag(stream,50706,4),1,2,0,0)||!Same(Tag(stream,50707,4),1,2,0,0)||
                First(stream,sensor,277,1)!=1||First(stream,sensor,339,1)!=1||
                !Same(TagOr(stream,50718,1,1),1,1)||!Same(Tag(stream,33421,2),2,2)||
                !Same(Tag(stream,33422,4),0,1,1,2)||!Same(TagOr(stream,50711,1),1)||
                !Same(TagOr(stream,50710,0,1,2),0,1,2)||!Same(TagOr(stream,50727,1,1,1),1,1,1)||
                !Same(Tag(stream,50778,1),17)||!Same(Tag(stream,50779,1),21)||
                Tag(stream,50721,9).Length!=9||Tag(stream,50722,9).Length!=9||Tag(stream,50728,3).Length!=3)
                throw DngLimit("该无损 JPEG DNG 的平面、版本或相机校准不在已验证范围内");
            Invert(Tag(stream,50721,9));Invert(Tag(stream,50722,9));
            foreach(double neutral in Tag(stream,50728,3))
                if(!(neutral>0&&neutral<=8))throw DngLimit("AsShotNeutral 通道值无效");
            foreach(int tag in new[]{50712,50715,50716,50879,50880,50964,50965,50974,50975,51125})
                if(Has(tag))throw DngLimit("该文件需要额外的感光校正、颜色映射或几何变换");
            return true;
        }

        internal DngLayout DescribeDng(Stream stream)
        {
            if (sensor == null || firstDirectory == null || !Properties.ContainsKey("dngVersion") || (long)Properties["photometric"] != 32803)
                throw DngLimit("缺少 DNG 标识和 Bayer CFA 感光目录");
            var version = Tag(stream, 50706, 4);
            if (version.Length != 4 || version[0] != 1 || version[1] > 7) throw DngLimit("未知的 DNG 版本");
            double[] backward = TagOr(stream,50707,1,0,0,0);
            if (sensorDirectories != 1 || backward.Length != 4 || backward[0] != 1 || backward[1] > 4)
                throw DngLimit("需要单一主感光平面及兼容 DNG 1.4 以前读取器的编码");
            int bits = (int)(long)Properties["bitsPerSample"];
            if ((bits != 12 && bits != 14 && bits != 16) || (long)Properties["compressionCode"] != 1 ||
                First(stream,sensor,277,1) != 1 || First(stream,sensor,266,1) != 1 || First(stream,sensor,339,1) != 1 ||
                First(stream,sensor,284,1) != 1 || First(stream,sensor,317,1) != 1 || sensor.ContainsKey(324) ||
                !sensor.ContainsKey(273) || !sensor.ContainsKey(279) || !Same(TagOr(stream,50974,1,1),1,1) || !Same(TagOr(stream,50975,1),1))
                throw DngLimit("需要 12、14 或 16 位的无压缩单平面整数条带");
            foreach (int incompatible in new[]{50712,50715,50716,51008,51009,51022})
                if (Has(incompatible)) throw DngLimit("包含尚未实现的传感器线性化、黑电平修正或 Opcode");
            if (!Same(TagOr(stream,50711,1),1) || !Same(TagOr(stream,50710,0,1,2),0,1,2) || !Same(Tag(stream,33421,2),2,2)
                || !Same(TagOr(stream,50718,1,1),1,1)) throw DngLimit("需要规则方形的 2×2 RGB Bayer 阵列");
            int[] pattern = Integers(Tag(stream,33422,4),4,"CFA 图案无效");
            if (!((pattern[0] == 1 && pattern[3] == 1 && pattern[1] + pattern[2] == 2 && pattern[1] != pattern[2]) ||
                (pattern[1] == 1 && pattern[2] == 1 && pattern[0] + pattern[3] == 2 && pattern[0] != pattern[3]))) throw DngLimit("不是三色 Bayer 阵列");
            int w = (int)(long)Properties["sensorWidth"], h = (int)(long)Properties["sensorHeight"];
            if ((long)w * h > 80000000 || w > 65535 || h > 65535) throw DngLimit("感光区超过本版预算");
            int[] area = Integers(TagOr(stream,50829,0,0,h,w),4,"ActiveArea 无效");
            int aw = area[3]-area[1], ah = area[2]-area[0];
            if (aw < 2 || ah < 2 || area[3] > w || area[2] > h || ((area[0] | area[1]) & 1) != 0) throw DngLimit("有效区域或 Bayer 起点不受支持");
            int[] origin = Integers(TagOr(stream,50719,0,0),2,"DefaultCropOrigin 无效");
            int[] cropSize = Integers(TagOr(stream,50720,aw,ah),2,"DefaultCropSize 无效");
            if (cropSize[0] < 1 || cropSize[1] < 1 || origin[0]+cropSize[0] > aw || origin[1]+cropSize[1] > ah) throw DngLimit("裁切区域超出有效感光区");
            double[] repeat = TagOr(stream,50713,1,1), black = TagOr(stream,50714,0);
            double[] white = TagOr(stream,50717,(1 << bits)-1);
            if (!(Same(repeat,1,1) && black.Length == 1) && !(Same(repeat,2,2) && black.Length == 4)) throw DngLimit("黑电平重复周期不受支持");
            if (white.Length != 1 || white[0] <= 0 || white[0] > (1 << bits)-1) throw DngLimit("白电平超出采样范围");
            var levels = new double[4]; var scale = new double[4];
            for (int i = 0; i < 4; i++)
            {
                levels[i] = black.Length == 1 ? black[0] : black[i];
                if (levels[i] < 0 || levels[i] >= white[0]) throw DngLimit("黑白电平次序无效");
                scale[i] = 1 / (white[0]-levels[i]);
            }
            int[] orientation = Integers(TagOr(stream,274,1),1,"方向无效");
            if (orientation[0] < 1 || orientation[0] > 8) throw DngLimit("方向无效");
            return new DngLayout { W=w,H=h,Left=area[1],Top=area[0],Right=area[3],Bottom=area[2],X=area[1]+origin[0],
                Y=area[0]+origin[1],Width=cropSize[0],Height=cropSize[1],Orientation=orientation[0],Pattern=pattern,Black=levels,
                InverseRange=scale,Color=CameraTransform(stream) };
        }

        internal BitmapSource DevelopDng(Stream stream, DngLayout d, int width, int height, CancellationToken cancel)
        {
            var bitmap = new WriteableBitmap(width,height,96,96,PixelFormats.Rgba64,null);
            var rows = new[] { new float[d.W],new float[d.W],new float[d.W] };
            var totals = new double[width*3]; var output = new ushort[width*4];
            var bins = new int[d.Width]; var columnCounts = new int[width];
            for (int x = 0; x < d.Width; x++) { int bin = (int)((long)x*width/d.Width); bins[x]=bin; columnCounts[bin]++; }
            int pending = -1, contributors = 0;
            Action flush = delegate
            {
                if (pending < 0) return;
                for (int x = 0; x < width; x++)
                {
                    int p = x*3, q = x*4;
                    double n = (double)columnCounts[x]*contributors;
                    double r = totals[p]/n, g=totals[p+1]/n, b=totals[p+2]/n;
                    for (int c = 0; c < 3; c++)
                    {
                        int i = c*3;
                        output[q+c] = ImagingRecipe.U16(ImagingRecipe.EncodeBounded(d.Color[i]*r+d.Color[i+1]*g+d.Color[i+2]*b));
                    }
                    output[q+3]=65535;
                }
                bitmap.WritePixels(new Int32Rect(0,pending,width,1),output,width*8,0);
                Array.Clear(totals,0,totals.Length); contributors=0;
            };
            Action<int> develop = delegate(int y)
            {
                if (y < d.Y || y >= d.Y+d.Height) return;
                int destY=(int)((long)(y-d.Y)*height/d.Height);
                if (pending != destY) { flush(); pending=destY; }
                contributors++;
                int up = y == d.Top ? y+1 : y-1, down = y+1 == d.Bottom ? y-1 : y+1;
                float[] center=rows[y%3], above=rows[up%3], below=rows[down%3];
                for (int dx = 0; dx < d.Width; dx++)
                {
                    int x=d.X+dx, left = x == d.Left ? x+1 : x-1, right = x+1 == d.Right ? x-1 : x+1;
                    int phase=((y&1)<<1)|(x&1), channel=d.Pattern[phase];
                    double middle=center[x], horizontal=.5*(center[left]+center[right]), vertical=.5*(above[x]+below[x]);
                    int p=bins[dx]*3;
                    if (channel == 1)
                    {
                        bool horizontalRed=d.Pattern[phase^1] == 0;
                        totals[p]+=horizontalRed ? horizontal : vertical; totals[p+1]+=middle; totals[p+2]+=horizontalRed ? vertical : horizontal;
                    }
                    else
                    {
                        double diagonal=.25*(above[left]+above[right]+below[left]+below[right]);
                        totals[p]+=channel == 0 ? middle : diagonal; totals[p+1]+=.5*(horizontal+vertical); totals[p+2]+=channel == 2 ? middle : diagonal;
                    }
                }
            };
            ReadSensorRows(stream,cancel,delegate(int y, ushort[] raw)
            {
                float[] normalized=rows[y%3]; int phase=(y&1)<<1;
                for (int x=d.Left; x<d.Right; x++)
                {
                    int p=phase|(x&1);
                    normalized[x]=(float)ImagingRecipe.Bound((raw[x]-d.Black[p])*d.InverseRange[p]);
                }
                if (y>0) develop(y-1);
            });
            develop(d.H-1); flush(); cancel.ThrowIfCancellationRequested();
            bitmap.Freeze(); return bitmap;
        }
    }
}
