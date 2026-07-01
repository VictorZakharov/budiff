using System.Collections.Generic;

namespace Budiff;

public static class DiffMetrics
{
    public static DiffChurn Churn(IReadOnlyList<DiffRow> rows)
    {
        var added = 0;
        var removed = 0;
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case DiffKind.Added:
                    added++;
                    break;
                case DiffKind.Removed:
                    removed++;
                    break;
                case DiffKind.Modified:
                    added++;
                    removed++;
                    break;
            }
        }

        return new DiffChurn(added, removed);
    }
}
