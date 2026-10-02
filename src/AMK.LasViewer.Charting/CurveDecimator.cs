namespace AMK.LasViewer.Charting;

public readonly record struct PlotPoint(double X, double Y);

/// <summary>
/// Строит ломаные кривой для видимого участка планшета.
/// </summary>
/// <remarks>
/// Когда точек больше, чем строк пикселей, в каждой строке оставляются первая,
/// минимальная, максимальная и последняя точки (алгоритм M4). Такая ломаная
/// на экране неотличима от полной: выбросы и размах сохраняются, а рисовать
/// приходится не больше четырёх точек на строку.
///
/// Пропуск (NaN) разрывает ломаную: соединять значения через пропуск — значит
/// показать данные, которых нет.
/// </remarks>
public static class CurveDecimator
{
    /// <param name="depths">Глубины по возрастанию.</param>
    /// <param name="values">Значения кривой в том же порядке.</param>
    /// <param name="toX">Перевод значения в координату X; NaN — точка не рисуется.</param>
    public static List<PlotPoint[]> Build(
        double[] depths,
        double[] values,
        DepthViewport viewport,
        Func<double, double> toX)
    {
        List<PlotPoint[]> segments = [];
        if (depths.Length == 0)
        {
            return segments;
        }

        // Одна точка за краем с каждой стороны: ломаная доходит до края окна,
        // а не обрывается на последнем видимом отсчёте.
        int first = Math.Max(0, LowerBound(depths, viewport.Top) - 1);
        int last = Math.Min(depths.Length - 1, LowerBound(depths, viewport.Bottom));
        if (first > last)
        {
            return segments;
        }

        int count = last - first + 1;
        bool decimate = count > viewport.HeightDip * 2;

        List<PlotPoint> current = [];
        void Flush()
        {
            if (current.Count > 0)
            {
                segments.Add(current.ToArray());
                current.Clear();
            }
        }

        if (!decimate)
        {
            for (int row = first; row <= last; row++)
            {
                double x = toX(values[row]);
                if (double.IsNaN(x))
                {
                    Flush();
                    continue;
                }

                current.Add(new PlotPoint(x, viewport.ToY(depths[row])));
            }

            Flush();
            return segments;
        }

        Bucket bucket = default;
        long bucketRow = long.MinValue;
        for (int row = first; row <= last; row++)
        {
            double y = viewport.ToY(depths[row]);
            double x = toX(values[row]);
            long pixelRow = (long)Math.Floor(y);

            if (pixelRow != bucketRow)
            {
                bucket.EmitTo(current);
                bucket = default;
                bucketRow = pixelRow;
            }

            if (double.IsNaN(x))
            {
                bucket.EmitTo(current);
                bucket = default;
                Flush();
                continue;
            }

            bucket.Add(new PlotPoint(x, y), row);
        }

        bucket.EmitTo(current);
        Flush();
        return segments;
    }

    private static int LowerBound(double[] depths, double depth)
    {
        int low = 0;
        int high = depths.Length;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (depths[middle] < depth)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Точки одной строки пикселей: первая, крайние по X и последняя.</summary>
    private struct Bucket
    {
        private int count;
        private PlotPoint firstPoint;
        private PlotPoint lastPoint;
        private PlotPoint minPoint;
        private PlotPoint maxPoint;
        private int minRow;
        private int maxRow;

        public void Add(PlotPoint point, int row)
        {
            if (count == 0)
            {
                firstPoint = lastPoint = minPoint = maxPoint = point;
                minRow = maxRow = row;
            }
            else
            {
                lastPoint = point;
                if (point.X < minPoint.X)
                {
                    minPoint = point;
                    minRow = row;
                }

                if (point.X > maxPoint.X)
                {
                    maxPoint = point;
                    maxRow = row;
                }
            }

            count++;
        }

        public readonly void EmitTo(List<PlotPoint> target)
        {
            if (count == 0)
            {
                return;
            }

            target.Add(firstPoint);
            if (count == 1)
            {
                return;
            }

            // Крайние точки выводятся в порядке следования по глубине, иначе
            // ломаная внутри строки пикселей пошла бы назад.
            (PlotPoint earlier, PlotPoint later) = minRow <= maxRow ? (minPoint, maxPoint) : (maxPoint, minPoint);
            AddDistinct(target, earlier);
            AddDistinct(target, later);
            AddDistinct(target, lastPoint);
        }

        private static void AddDistinct(List<PlotPoint> target, PlotPoint point)
        {
            if (target[^1] != point)
            {
                target.Add(point);
            }
        }
    }
}
