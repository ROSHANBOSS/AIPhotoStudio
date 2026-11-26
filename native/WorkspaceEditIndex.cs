using System;
using System.Collections.Generic;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace
    {
        private void BuildEditIndex()
        {
            db.Run("CREATE TABLE IF NOT EXISTS edited_frames(photo_id TEXT NOT NULL,seq INTEGER NOT NULL,PRIMARY KEY(photo_id,seq),FOREIGN KEY(photo_id,seq) REFERENCES history(photo_id,seq) ON DELETE CASCADE)");
            db.Run("DELETE FROM edited_frames");
            long last=0;
            while(true)
            {
                var rows=db.Rows("SELECT rowid,photo_id,seq,parameters FROM history WHERE rowid>? AND seq>0 ORDER BY rowid LIMIT 32",last);
                if(rows.Count==0)break;
                foreach(var row in rows)
                {
                    last=(long)row[0];
                    IndexEditFrame((string)row[1],(long)row[2],json.Deserialize<Dictionary<string,double>>((string)row[3]));
                }
            }
        }

        private void IndexEditFrame(string id,long seq,IDictionary<string,double> values)
        {
            foreach(var value in values)
            {
                ControlSpec control;
                if(!controls.TryGetValue(value.Key,out control)||control.initial==value.Value)continue;
                if(control.group==PortraitEdits.RegionGroup||control.group==PortraitControls.ResourceGroup||
                    control.group=="人体资源"||control.group=="分区资源"||control.group=="LUT 资源")continue;
                db.Run("INSERT INTO edited_frames(photo_id,seq) VALUES(?,?)",id,seq);
                break;
            }
        }
    }
}
