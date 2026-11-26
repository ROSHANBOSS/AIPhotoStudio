using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace
    {
        private void CreateSetReviewTable()
        {
            db.Run("CREATE TABLE IF NOT EXISTS photo_review (photo_id TEXT PRIMARY KEY REFERENCES photos(id), mark TEXT NOT NULL DEFAULT 'none', blur TEXT NOT NULL DEFAULT 'auto', eyes TEXT NOT NULL DEFAULT 'auto', evidence TEXT)");
        }

        private void CreateSetReviewEyesTable()
        {
            db.Run("CREATE TABLE IF NOT EXISTS photo_eye_reviews (photo_id TEXT NOT NULL REFERENCES photos(id), source TEXT NOT NULL, frame INTEGER NOT NULL, face_key TEXT NOT NULL, verdict TEXT NOT NULL CHECK(verdict IN ('flag','clear','unknown')), PRIMARY KEY(photo_id,source,frame,face_key))");
        }

        internal int SetReviewSensitivity(Dictionary<string,object> args)
        {
            Fields(args,"level");
            lock(gate) {
                const string setting="review-sensitivity";
                if(args.ContainsKey("level")) {
                    long level=IntegerArg(args,"level");
                    if(level>2) throw new ArgumentException("检测灵敏度超出范围。");
                    db.Run("INSERT OR REPLACE INTO settings(name,value) VALUES(?,?)",setting,level.ToString(CultureInfo.InvariantCulture));
                    return (int)level;
                }
                int stored;
                return Int32.TryParse((string)db.Scalar("SELECT value FROM settings WHERE name=?",setting),out stored) && stored>=0 && stored<=2?stored:1;
            }
        }

        internal sealed class SetPhoto
        {
            internal string Id, Path;
            internal long Revision;
            internal Dictionary<string,double> Values;
            internal string[] Locks;
        }

        internal SetPhoto SetPhotoAt(Dictionary<string,object> args)
        {
            lock(gate) return db.Transaction(false,delegate {
                var state=Editable(args);
                return new SetPhoto { Id=state.Id, Path=PathFor(state.Id), Revision=state.Revision,
                    Values=state.Values, Locks=OrderedLocks(state.Locks) };
            });
        }

        internal object SetReviewRows(Dictionary<string,object> args)
        {
            Fields(args,"ids","marksOnly","exposure");
            var items=Sequence(Arg(args,"ids"),400);
            var ids=items.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            if(ids.Length!=items.Count || ids.Any(id=>id.Length>100)) throw new ArgumentException("挑片列表无效。");
            if(ids.Length==0) return new object[0];
            bool marks=args.ContainsKey("marksOnly") && BoolArg(args,"marksOnly");
            bool grid=args.ContainsKey("exposure") && BoolArg(args,"exposure");
            lock(gate) {
                string slots=String.Join(",",ids.Select(id=>"?"));
                string columns=marks&&!grid?"NULL,NULL FROM photo_review q":"q.evidence,f.sha256 FROM photo_review q LEFT JOIN photo_sources f ON f.photo_id=q.photo_id";
                var rows=db.Rows("SELECT q.photo_id,q.mark,q.blur,q.eyes,"+columns+" WHERE q.photo_id IN ("+slots+") AND q.photo_id NOT IN (SELECT photo_id FROM removed_photos)",ids);
                int level=grid?SetReviewSensitivity(new Dictionary<string,object>()):1;
                var overrides=(marks?new List<object[]>():db.Rows("SELECT photo_id,source,frame,face_key,verdict FROM photo_eye_reviews WHERE photo_id IN ("+slots+")",ids)).ToLookup(r=>(string)r[0]);
                return rows.Select(r=>{
                    var evidence=r[4]==null?null:json.Deserialize<SetEvidence>((string)r[4]);
                    // Older analyses remain readable until reanalysis. v3 already has source-bound face anchors.
                    if(evidence!=null && ((evidence.version<2 || evidence.version>SetReviewPixels.Version) || !MatchesRegisteredSource(evidence.source,r[5])))
                        evidence=null;
                    if(!marks)AttachEyeReviews(evidence,overrides[(string)r[0]]);
                    var q=grid&&evidence!=null&&evidence.quality!=null?evidence.quality[level]:null;
                    return new {id=r[0],mark=r[1],blur=r[2],eyes=r[3],evidence=marks?null:evidence,
                        exposure=q==null?null:new {frame=evidence.frame,over=q.over,under=q.under}};
                }).ToArray();
            }
        }

        private static bool MatchesRegisteredSource(string hex,object registered)
        {
            return registered==null || String.Equals(hex,BitConverter.ToString(Convert.FromBase64String((string)registered)).Replace("-",""),StringComparison.OrdinalIgnoreCase);
        }

        private static bool NearEyes(ReviewFace current,string storedKey,double aspect)
        {
            // A fresh mesh of the same subject can shift by a few source pixels.
            var parts=storedKey.Split('-');
            if(parts.Length!=4)return false;
            var points=new double[4];
            for(int i=0;i<4;i++) {
                int n;
                if(!Int32.TryParse(parts[i],out n)||n<0||n>65535)return false;
                points[i]=n/65535d;
            }
            var c=current.centers;
            double spanX=(c[0]-c[2])*aspect,spanY=c[1]-c[3];
            double tolerance=.04*(spanX*spanX+spanY*spanY);
            for(int i=0;i<4;i+=2) {
                double dx=(points[i]-c[i])*aspect,dy=points[i+1]-c[i+1];
                if(dx*dx+dy*dy>tolerance)return false;
            }
            return true;
        }

        // Return the durable anchor keys for updates as well as attaching verdicts for display.
        private static Dictionary<string,string> AttachEyeReviews(SetEvidence e,IEnumerable<object[]> rows)
        {
            var anchors=new Dictionary<string,string>();
            if(e==null || e.faces==null)return anchors;
            var choices=rows.Where(r=>(string)r[1]==e.source && (long)r[2]==e.frame).ToArray();
            foreach(var face in e.faces) {
                face.manual="auto";
                var chosen=choices.FirstOrDefault(r=>(string)r[3]==face.key);
                if(chosen==null) {
                    var nearby=choices.Where(r=>NearEyes(face,(string)r[3],e.aspect) &&
                        e.faces.Count(f=>NearEyes(f,(string)r[3],e.aspect))==1).Take(2).ToArray();
                    if(nearby.Length==1)chosen=nearby[0];
                }
                if(chosen!=null) {anchors[face.key]=(string)chosen[3];face.manual=(string)chosen[4];}
            }
            return anchors;
        }

        private List<object[]> EyeReviewsFor(string id,SetEvidence evidence)
        {
            return db.Rows("SELECT photo_id,source,frame,face_key,verdict FROM photo_eye_reviews WHERE photo_id=? AND source=? AND frame=?",id,evidence.source,evidence.frame);
        }

        internal object SetReviewEye(Dictionary<string,object> args)
        {
            Fields(args,"id","revision","source","frame","key","value");
            string id=TextArg(args,"id",100),source=TextArg(args,"source",64),key=TextArg(args,"key",64),value=TextArg(args,"value",12);
            long revision=IntegerArg(args,"revision"),frame=IntegerArg(args,"frame");
            if(!new[]{"auto","flag","clear","unknown"}.Contains(value))throw new ArgumentException("未知的逐脸复核结论。");
            lock(gate) return db.Transaction(true,delegate {
                CheckSetPhoto(id,revision);
                var current=ReviewEvidenceFor(id);
                if(current==null || current.version<3 || current.version>SetReviewPixels.Version || current.source!=source || current.frame!=frame ||
                    current.faces==null || !current.faces.Any(f=>f.key==key) ||
                    CameraRawModes.Selected(Editable(args).Values)!=frame || BodyAssets.SourceDigest(PathFor(id))!=source)
                    throw new InvalidOperationException("眼睛位置或原片已经改变，请重新分析。");
                var anchors=AttachEyeReviews(current,EyeReviewsFor(id,current));
                string anchor;
                if(!anchors.TryGetValue(key,out anchor))anchor=key;
                if(value=="auto")db.Run("DELETE FROM photo_eye_reviews WHERE photo_id=? AND source=? AND frame=? AND face_key=?",id,source,frame,anchor);
                else db.Run("INSERT OR REPLACE INTO photo_eye_reviews(photo_id,source,frame,face_key,verdict) VALUES(?,?,?,?,?)",id,source,frame,anchor,value);
                return value;
            });
        }

        internal void SetReviewField(Dictionary<string,object> args)
        {
            Fields(args,"id","field","value");
            string id=TextArg(args,"id",100), field=TextArg(args,"field",10), value=TextArg(args,"value",16);
            if(field!="mark" && field!="blur" && field!="eyes") throw new ArgumentException("未知的复核项目。");
            if(!(field=="mark"?new[]{"none","keep","reject"}:new[]{"auto","flag","clear"}).Contains(value))
                throw new ArgumentException("未知的复核结论。");
            lock(gate) db.Transaction(true,delegate {
                RequirePhoto(id);
                db.Run("INSERT OR IGNORE INTO photo_review(photo_id) VALUES(?)",id);
                db.Run("UPDATE photo_review SET "+field+"=? WHERE photo_id=?",value,id);
                return true;
            });
        }

        internal SetEvidence ReviewEvidenceFor(string id)
        {
            lock(gate) {
                var value=(string)db.Scalar("SELECT evidence FROM photo_review WHERE photo_id=?",id);
                var evidence=value==null?null:json.Deserialize<SetEvidence>(value);
                if(evidence!=null && evidence.faces!=null)AttachEyeReviews(evidence,EyeReviewsFor(id,evidence));
                return evidence;
            }
        }

        internal void CheckSetPhoto(string id,long revision)
        {
            lock(gate) {
                var value=db.Scalar("SELECT revision FROM photos WHERE id=? AND id NOT IN (SELECT photo_id FROM removed_photos)",id);
                if(value==null || (long)value!=revision) throw new InvalidOperationException("照片已有新的编辑版本，请重新分析。");
            }
        }

        internal void SaveSetEvidence(SetPhoto photo,SetEvidence evidence)
        {
            lock(gate) db.Transaction(true,delegate {
                CheckSetPhoto(photo.Id,photo.Revision);
                if(!MatchesRegisteredSource(evidence.source,db.Scalar("SELECT sha256 FROM photo_sources WHERE photo_id=?",photo.Id)))
                    throw new InvalidOperationException("原片与登记的文件不符，请先找回原片。");
                db.Run("INSERT OR IGNORE INTO photo_review(photo_id) VALUES(?)",photo.Id);
                db.Run("UPDATE photo_review SET evidence=? WHERE photo_id=?",json.Serialize(evidence),photo.Id);
                if(evidence.faces!=null)AttachEyeReviews(evidence,EyeReviewsFor(photo.Id,evidence));
                return true;
            });
        }
    }
}
