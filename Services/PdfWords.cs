using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace TowerProcessSheet.Services;

/// <summary>
/// Page words with "stacked text" removed.
/// Some drawings carry a revision as a second text object printed exactly on top of the old one
/// (old "ACD1" under new "ACD2", or the same word drawn twice to look bold). Reading the letters
/// by position then interleaves both strings — "AACCDD12". Here, when two letters sit on the same
/// spot, only the one drawn last (the one on top, i.e. the visible one) is kept.
/// </summary>
public static class PdfWords
{
    public static List<Word> Get(Page page)
        => NearestNeighbourWordExtractor.Instance.GetWords(CollapseStacked(page.Letters)).ToList();

    public static List<Letter> CollapseStacked(IEnumerable<Letter> letters)
    {
        const double cell = 4.0;
        var kept = new List<Letter?>();
        var grid = new Dictionary<(int, int), List<int>>();

        foreach (var l in letters)
        {
            if (string.IsNullOrWhiteSpace(l.Value))
            {
                kept.Add(l);
                continue;
            }

            var r = l.GlyphRectangle;
            double w = r.Right - r.Left, h = r.Top - r.Bottom;
            double cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            int gx = (int)Math.Floor(cx / cell), gy = (int)Math.Floor(cy / cell);

            int replaced = -1;
            if (w > 0.01 && h > 0.01)
            {
                for (int dx = -1; dx <= 1 && replaced < 0; dx++)
                    for (int dy = -1; dy <= 1 && replaced < 0; dy++)
                    {
                        if (!grid.TryGetValue((gx + dx, gy + dy), out var ids)) continue;
                        foreach (var id in ids)
                        {
                            var e = kept[id];
                            if (e == null) continue;
                            var er = e.GlyphRectangle;
                            double ew = er.Right - er.Left, eh = er.Top - er.Bottom;
                            if (ew <= 0.01 || eh <= 0.01) continue;
                            double ox = Math.Min(r.Right, er.Right) - Math.Max(r.Left, er.Left);
                            double oy = Math.Min(r.Top, er.Top) - Math.Max(r.Bottom, er.Bottom);
                            if (ox <= 0 || oy <= 0) continue;
                            if (ox / Math.Min(w, ew) >= 0.6 && oy / Math.Min(h, eh) >= 0.6)
                            {
                                replaced = id;
                                break;
                            }
                        }
                    }
            }

            if (replaced >= 0)
            {
                kept[replaced] = l; // the later-drawn letter is on top — it wins
            }
            else
            {
                kept.Add(l);
                int id = kept.Count - 1;
                if (!grid.TryGetValue((gx, gy), out var list)) grid[(gx, gy)] = list = new List<int>();
                list.Add(id);
            }
        }

        return kept.Where(k => k != null).Select(k => k!).ToList();
    }
}