using System;
using System.Collections.Generic;
using System.Linq;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace
    {
        internal sealed class SetMatchRow
        {
            internal string Id, Digest, Request, MaskSource;
            internal long Revision;
            internal int Slot;
            internal bool NeedsPreview,Previewed;
            internal Dictionary<string,double> Patch;
            internal SetMatchRegions.Layer[] Layers;
            internal SetMatchRegions.Layer[] ChangedLayers()
            {
                return Layers==null?null:Layers.Where(l=>SetMatchRegions.Tones.Any(t=>Patch.ContainsKey(LocalEdits.ToneKey(l.Slot,t)))).ToArray();
            }
        }

        internal object CommitSetMatch(string reference,long revision,string digest,IList<SetMatchRow> rows)
        {
            if(rows.Count==0) throw new ArgumentException("请勾选至少一张有建议参数的照片。");
            lock(gate) return db.Transaction(true,delegate {
                CheckSetPhoto(reference,revision);
                if(BodyAssets.SourceDigest(PathFor(reference))!=digest) throw new InvalidOperationException("基准原片已改变，请重新拟合。");
                var pending=new List<SetMatchRow>();
                foreach(var row in rows) {
                    if(row.Id==reference || row.Patch.Count==0) throw new InvalidOperationException("拟合目标无效。");
                    if(BodyAssets.SourceDigest(PathFor(row.Id))!=row.Digest) throw new InvalidOperationException("目标原片已改变，请重新拟合。");
                    // Server-issued row request IDs only ever identify one immutable proposal.
                    if(db.Scalar("SELECT 1 FROM requests WHERE photo_id=? AND request_id=?",row.Id,row.Request)!=null)continue;
                    CheckSetPhoto(row.Id,row.Revision);pending.Add(row);
                }
                foreach(var row in pending) {
                    var patch=new Dictionary<string,double>(row.Patch);
                    bool regions=row.Layers!=null;
                    var allowed=regions?row.Layers.SelectMany(l=>SetMatchRegions.Tones.Select(t=>LocalEdits.ToneKey(l.Slot,t))).ToList():
                        new List<string>{"exposure","temperature","tint"};
                    var changedLayers=row.ChangedLayers();
                    if(regions&&changedLayers.Any(l=>l.Added)) {
                        var mask=SetMatchRegions.Draft(Current(row.Id).Values,changedLayers);
                        foreach(var field in LocalEdits.SaveSet(mask,row.MaskSource)){allowed.Add(field.Key);patch.Add(field.Key,field.Value);}
                    }
                    Commit(new Dictionary<string,object>{{"id",row.Id},{"revision",row.Revision},{"requestId",row.Request},
                        {"source","set-match"},{"label",regions?"整组校色 · "+String.Join("、",row.Layers.Select(l=>SetMatchRegions.Name(l.Area.Role)))+"局部匹配":"整组校色 · 匹配基准片"},
                        {"values",patch},{"allowedKeys",allowed.ToArray()}});
                }
                if(BodyAssets.SourceDigest(PathFor(reference))!=digest||pending.Any(r=>BodyAssets.SourceDigest(PathFor(r.Id))!=r.Digest))
                    throw new InvalidOperationException("确认期间原片已改变，请重新拟合。");
                return ReadSnapshot();
            });
        }
    }
}
