using System;
using System.Collections.Generic;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace
    {
        private void SetPhotoNote(Dictionary<string,object> args)
        {
            Fields(args,"id","revision","taken","caption");
            State photo=Editable(args);
            string taken=Arg(args,"taken") as string, caption=Arg(args,"caption") as string;
            if(taken==null||caption==null||caption.Length>500||caption.IndexOf((char)0)>=0)throw new ArgumentException("说明最多可填写 500 字。");
            string padded=taken.Length==4?taken+"-01-01":taken.Length==7?taken+"-01":taken;
            DateTime day;
            if(taken!=""&&!DateTime.TryParseExact(padded,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out day))
                throw new ArgumentException("请按年、年月或年月日填写，例如 2008、2008-07 或 2008-07-15。");
            caption=caption.Trim();
            var old=db.Rows("SELECT taken,caption FROM photo_notes WHERE photo_id=?",photo.Id);
            if(old.Count==0?(taken==""&&caption==""):(Equals(old[0][0],taken)&&Equals(old[0][1],caption)))return;
            if(taken==""&&caption=="")db.Run("DELETE FROM photo_notes WHERE photo_id=?",photo.Id);
            else db.Run("INSERT OR REPLACE INTO photo_notes(photo_id,taken,caption) VALUES(?,?,?)",photo.Id,taken,caption);
            db.Run("UPDATE photos SET revision=revision+1 WHERE id=?",photo.Id);
        }

        // Called within Dispatch's write transaction. The original and its edit history stay available for reimport.
        private void SetLibraryMembership(string method, Dictionary<string,object> args)
        {
            bool remove = method == "removePhoto";
            Fields(args, "id", "revision", "nextId");
            string id = TextArg(args, "id", 100);
            long revision = IntegerArg(args, "revision");
            var rows = db.Rows("SELECT revision, EXISTS(SELECT 1 FROM removed_photos WHERE photo_id=photos.id) FROM photos WHERE id=?", id);
            if (rows.Count == 0) throw new KeyNotFoundException("图库中找不到这张照片。");
            long actual = (long)rows[0][0];
            bool hidden = (long)rows[0][1] != 0;
            if (hidden == remove && actual == revision + 1) return;
            if (hidden == remove || actual != revision) throw new InvalidOperationException("照片修订已改变，请刷新图库后重试。");
            db.Run("UPDATE photos SET revision=revision+1 WHERE id=?", id);
            if (!remove)
            {
                db.Run("DELETE FROM removed_photos WHERE photo_id=?", id);
                SetSelected(id);
                return;
            }
            db.Run("INSERT INTO removed_photos(photo_id) VALUES(?)", id);
            if (!Equals(id, db.Scalar("SELECT value FROM settings WHERE name='selected'"))) return;
            string next = args.ContainsKey("nextId") ? TextArg(args, "nextId", 100) : null;
            if (next == null || db.Scalar("SELECT 1 FROM photos WHERE id=? AND id NOT IN (SELECT photo_id FROM removed_photos)", next) == null)
                next = (string)db.Scalar("SELECT id FROM photos WHERE id NOT IN (SELECT photo_id FROM removed_photos) ORDER BY (rowid>(SELECT rowid FROM photos WHERE id=?)) DESC, rowid LIMIT 1", id);
            if (next == null) db.Run("DELETE FROM settings WHERE name='selected'");
            else SetSelected(next);
        }
    }
}
