using System;

namespace AIPhotoStudio
{
    internal sealed class ImagingDetail
    {
        internal readonly int Radius;
        private readonly int coarseRadius;
        private readonly double noise, sharp, clarity, chromaNoise, lumaNoise;

        internal ImagingDetail(ImagingRecipe recipe, int viewEdge, int sourceEdge)
        {
            double scale = (double)viewEdge / sourceEdge;
            double sourceRadius = Math.Max(2, Math.Min(24, sourceEdge * .005));
            double scaledRadius = sourceRadius * scale;
            coarseRadius = Math.Max(1, (int)Math.Round(scaledRadius));
            clarity = recipe.Clarity * Math.Min(1, scaledRadius);
            noise = recipe.Noise * Math.Min(1, scale);
            chromaNoise=recipe.ChromaNoise*Math.Min(1,scale);
            lumaNoise=recipe.LumaNoise*Math.Min(1,scale);
            sharp = recipe.Sharpness * Math.Min(1, scale);
            Radius=Math.Max(clarity!=0?coarseRadius:0,chromaNoise>0||lumaNoise>0?2:noise!=0||sharp!=0?1:0);
        }

        // Estimate chroma relative to luminance. Common-mode adjustments leave the RGB
        // differences intact; chroma corrections have zero weighted luminance.
        private void SeparateNoise(ushort[] source,int width,int height,int x,int y,double centerY,
            ref double r,ref double g,ref double b)
        {
            const double greenWeight=.7152,redWeight=.2126,blueWeight=.0722;
            double redDiff=0,blueDiff=0,meanY=0,chromaWeight=0,lumaWeight=0;
            for(int dy=-2;dy<=2;dy++)
            {
                int row=Limit(y+dy,height)*width;
                for(int dx=-2;dx<=2;dx++)
                {
                    int p=(row+Limit(x+dx,width))*4;
                    double spatial=(3-Math.Abs(dx))*(3-Math.Abs(dy))*(source[p+3]/65535.0);
                    double lum=Lum(source,p),dist=lum-centerY;
                    if(chromaNoise>0)
                    {
                        double t=dist/(3200+4000*chromaNoise),w=spatial/(1+t*t*t*t);
                        redDiff+=w*(source[p]-lum);blueDiff+=w*(source[p+2]-lum);chromaWeight+=w;
                    }
                    if(lumaNoise>0)
                    {
                        double t=dist/(1300+4400*lumaNoise),w=spatial/(1+t*t*t*t);
                        meanY+=w*lum;lumaWeight+=w;
                    }
                }
            }
            if(chromaWeight>0)
            {
                double oldY=ImagingRecipe.Luma(r,g,b);
                double dr=chromaNoise*(redDiff/chromaWeight-r+oldY),db=chromaNoise*(blueDiff/chromaWeight-b+oldY);
                r+=dr;b+=db;g-=(redWeight*dr+blueWeight*db)/greenWeight;
            }
            if(lumaWeight>0){double d=lumaNoise*(meanY/lumaWeight-centerY);r+=d;g+=d;b+=d;}
        }

        private static int Limit(int pos, int size) { return pos < 0 ? 0 : pos >= size ? size - 1 : pos; }
        private static double Lum(ushort[] src, int p)
        {
            return ImagingRecipe.Luma(src[p], src[p + 1], src[p + 2]);
        }

        private void FineMean(ushort[] src, int width, int height, int x, int y, int center, double centerY,
            out double red, out double green, out double blue, out double blurredY)
        {
            double sumR = 0, sumG = 0, sumB = 0, sumYW = 0, weight = 0, unweighted = 0;
            double threshold = 1500 + noise * 4800;
            for (int dy = -1; dy <= 1; dy++)
            {
                int row = Limit(y + dy, height) * width;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int p = (row + Limit(x + dx, width)) * 4;
                    double a = src[p + 3] / 65535.0;
                    double spatial = (dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1) * a;
                    double lum = Lum(src, p);
                    unweighted += spatial;
                    sumYW += lum * spatial;
                    if (noise == 0) continue;
                    double cd = Math.Max(Math.Abs(src[center] - src[p]), Math.Max(Math.Abs(src[center + 1] - src[p + 1]), Math.Abs(src[center + 2] - src[p + 2])));
                    double distance = (.65 * Math.Abs(centerY - lum) + .35 * cd) / threshold;
                    double w = spatial / (1 + distance * distance * (1 + distance * distance));
                    weight += w;
                    sumR += src[p] * w; sumG += src[p + 1] * w; sumB += src[p + 2] * w;
                }
            }
            red = weight > 0 ? sumR / weight : src[center];
            green = weight > 0 ? sumG / weight : src[center + 1];
            blue = weight > 0 ? sumB / weight : src[center + 2];
            blurredY = unweighted > 0 ? sumYW / unweighted : centerY;
        }

        private double CoarseMean(ushort[] src, int width, int height, int x, int y, double centerY)
        {
            double sum = 0, weights = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                int row = Limit(y + dy * coarseRadius, height) * width;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int p = (row + Limit(x + dx * coarseRadius, width)) * 4;
                    double w = (dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1) * (src[p + 3] / 65535.0);
                    sum += Lum(src, p) * w;
                    weights += w;
                }
            }
            return weights > 0 ? sum / weights : centerY;
        }

        private static ushort Sample(double v) { return (ushort)(v <= 0 ? 0 : v >= 65535 ? 65535 : v + .5); }

        internal void Row(ushort[] src, int width, int height, int y, ushort[] dest, int destStart)
        {
            int p = y * width * 4;
            for (int x = 0; x < width; x++, p += 4, destStart += 4)
            {
                double r = src[p], g = src[p + 1], b = src[p + 2];
                ushort a = src[p + 3];
                if (a != 0)
                {
                    double luma = ImagingRecipe.Luma(r, g, b), change = 0;
                    if (noise != 0 || sharp != 0)
                    {
                        double meanR, meanG, meanB, soft;
                        FineMean(src, width, height, x, y, p, luma, out meanR, out meanG, out meanB, out soft);
                        r += (meanR - r) * noise; g += (meanG - g) * noise; b += (meanB - b) * noise;
                        double fine = luma - soft;
                        change += sharp * 1.5 * fine / (1 + Math.Abs(fine) / 6500);
                    }
                    if (clarity != 0)
                    {
                        double broad = luma - CoarseMean(src, width, height, x, y, luma);
                        double level = luma / 65535.0;
                        change += clarity * (.28 + 2.9 * level * (1 - level)) * broad / (1 + Math.Abs(broad) / 11500);
                    }
                    if(chromaNoise>0||lumaNoise>0)SeparateNoise(src,width,height,x,y,luma,ref r,ref g,ref b);
                    r += change; g += change; b += change;
                }
                dest[destStart] = Sample(r); dest[destStart + 1] = Sample(g);
                dest[destStart + 2] = Sample(b); dest[destStart + 3] = a;
            }
        }
    }
}
