using System.Collections.Generic;
using Microsoft.VisualStudio.Services.Common;

namespace Common
{
    public class RevAndPhaseStatus
    {
        public const string IGNORED_COMMENT = "IGNORE";

        public int Rev { get; set; }

        public ISet<string> PhaseStatus { get; set; }

        public RevAndPhaseStatus()
        {
            this.PhaseStatus = new HashSet<string>();
        }

        public RevAndPhaseStatus(string revAndPhaseStatusComment)
        {
            SetRevAndPhaseStatus(revAndPhaseStatusComment);
        }

        public void SetRevAndPhaseStatus(string revAndPhaseStatusComment)
        {
            string[] parts = revAndPhaseStatusComment.Split(';');
            int revIndex = 0;
            if (!int.TryParse(parts[revIndex], out int rev))
            {
                revIndex++;
                rev = int.Parse(parts[revIndex]);
            }
            this.Rev = rev;
            string[] phaseStatusStringParts = parts.SubArray(revIndex + 1, parts.Length - revIndex - 1);
            this.PhaseStatus = phaseStatusStringParts.ToHashSet();
        }

        public string GetCommentRepresentation()
        {
            return $"{IGNORED_COMMENT};{this.Rev};{string.Join(";", this.PhaseStatus)}";
        }
    }
}
