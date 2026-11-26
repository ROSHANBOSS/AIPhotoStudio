using System;
using System.Collections.Generic;
using System.Linq;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace
    {
        private const string ProposalPrefix = "proposal-";
        private sealed class AdjustmentOffer
        {
            internal string Photo, Request;
            internal long Revision;
            internal Dictionary<string,double> Values;
            internal string[][] Groups;
        }
        private readonly List<AdjustmentOffer> adjustmentOffers = new List<AdjustmentOffer>();

        private static string ChoiceFor(string key)
        {
            if (key.StartsWith("curve_",StringComparison.Ordinal) ||
                key.StartsWith("grade_",StringComparison.Ordinal) && (key.EndsWith("_hue") || key.EndsWith("_sat")))
                return key.Substring(0,key.LastIndexOf('_'));
            return key;
        }

        private void RequireProposalPhoto(string id)
        {
            if (!Equals(id,db.Scalar("SELECT value FROM settings WHERE name='selected'")))
                throw new InvalidOperationException("提案的照片已切换，请根据当前照片重新生成。");
        }

        internal object IssueProposal(Dictionary<string,object> args,Dictionary<string,double> values,string label,string message)
        {
            lock (gate) return db.Transaction(false,delegate {
                var request=new Dictionary<string,object>{{"id",args["id"]},{"revision",args["revision"]},{"values",values}};
                State s=Editable(request);
                RequireProposalPhoto(s.Id);
                Dictionary<string,double> patch;
                PreviewParameters(s,request,out patch);
                var changes=patch.Where(p=>p.Value!=s.Values[p.Key]).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
                if (changes.Count==0) throw new InvalidOperationException("提案中的参数与当前照片相同。");
                var offer=new AdjustmentOffer {
                    Photo=s.Id,Revision=s.Revision,Request=ProposalPrefix+Guid.NewGuid().ToString("N"),Values=changes,
                    Groups=changes.Keys.GroupBy(ChoiceFor).Select(group=>group.ToArray()).ToArray()
                };
                adjustmentOffers.RemoveAll(p=>p.Photo!=s.Id || p.Revision!=s.Revision);
                // A window displays one card. Keep other recently issued candidates at this revision for in-flight callers.
                if (adjustmentOffers.Count==32) adjustmentOffers.RemoveAt(0);
                adjustmentOffers.Add(offer);
                return new {id=s.Id,revision=s.Revision,requestId=offer.Request,values=changes,groups=offer.Groups,label=label,message=message};
            });
        }

        internal bool DiscardProposal(Dictionary<string,object> args)
        {
            Fields(args,"id","revision","requestId");
            string id=TextArg(args,"id",100),request=TextArg(args,"requestId",128);
            long revision=IntegerArg(args,"revision");
            lock (gate) return adjustmentOffers.RemoveAll(p=>p.Photo==id && p.Revision==revision && p.Request==request)>0;
        }

        private void CheckIssuedSelection(State s,string request,Dictionary<string,double> patch)
        {
            RequireProposalPhoto(s.Id);
            var offer=adjustmentOffers.Find(p=>p.Request==request && p.Photo==s.Id && p.Revision==s.Revision);
            if (offer==null) throw new InvalidOperationException("提案已过期或已取消，请重新生成。");
            foreach (var pair in patch)
            {
                double original;
                if (!offer.Values.TryGetValue(pair.Key,out original) || original!=pair.Value)
                    throw new InvalidOperationException("只能应用原提案给出的参数和值，请重新勾选。");
            }
            foreach (var group in offer.Groups)
                if (group.Any(patch.ContainsKey) && !group.All(patch.ContainsKey))
                    throw new InvalidOperationException("提案中相关联的参数需要一起应用。");
        }
    }
}
