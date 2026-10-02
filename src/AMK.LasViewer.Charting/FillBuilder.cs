namespace AMK.LasViewer.Charting;

public sealed record FillPolygon(PlotPoint[] Points, uint Color);

/// <summary>
/// Многоугольники заливки между кривой и границей: краем трека, значением
/// или другой кривой.
/// </summary>
/// <remarks>
/// Заливка строится по одной выборке на строку пикселей: форма заливки на
/// экране от этого не меняется, а число точек не зависит от длины записи.
/// Пропуск в кривой или в границе прерывает заливку. В точке пересечения
/// кривой и границы многоугольник замыкается, и дальше начинается
/// многоугольник другого цвета — так подсвечиваются пересечения кривых.
/// </remarks>
public static class FillBuilder
{
    /// <param name="toX">Координата X кривой по значению; NaN — пропуск.</param>
    /// <param name="boundaryX">Координата X границы для строки данных; NaN — пропуск.</param>
    /// <param name="positiveColor">Цвет, где кривая правее границы.</param>
    /// <param name="negativeColor">Цвет, где кривая левее границы; <c>null</c> — не заливать.</param>
    public static List<FillPolygon> Build(
        double[] depths,
        double[] values,
        Func<double, double> toX,
        Func<int, double> boundaryX,
        DepthViewport viewport,
        uint positiveColor,
        uint? negativeColor)
    {
        List<FillPolygon> polygons = [];
        if (depths.Length == 0)
        {
            return polygons;
        }

        int first = Math.Max(0, LowerBound(depths, viewport.Top) - 1);
        int last = Math.Min(depths.Length - 1, LowerBound(depths, viewport.Bottom));
        if (first > last)
        {
            return polygons;
        }

        int stride = Math.Max(1, (int)((last - first + 1) / Math.Max(1, viewport.HeightDip)));

        List<(double Y, double A, double B)> run = [];
        int runSign = 0;

        void Flush()
        {
            if (run.Count >= 2 && runSign != 0)
            {
                uint? color = runSign > 0 ? positiveColor : negativeColor;
                if (color is { } fill)
                {
                    PlotPoint[] points = new PlotPoint[run.Count * 2];
                    for (int index = 0; index < run.Count; index++)
                    {
                        points[index] = new PlotPoint(run[index].A, run[index].Y);
                        points[^(index + 1)] = new PlotPoint(run[index].B, run[index].Y);
                    }

                    polygons.Add(new FillPolygon(points, fill));
                }
            }

            run.Clear();
            runSign = 0;
        }

        for (int row = first; ; row += stride)
        {
            if (row > last)
            {
                // Последняя строка берётся всегда, иначе заливка не дойдёт до края окна.
                if (row - stride == last)
                {
                    break;
                }

                row = last;
            }

            double y = viewport.ToY(depths[row]);
            double a = toX(values[row]);
            double b = boundaryX(row);
            if (double.IsNaN(a) || double.IsNaN(b))
            {
                Flush();
            }
            else
            {
                double difference = a - b;
                int sign = Math.Abs(difference) < 1e-9 ? 0 : Math.Sign(difference);
                if (run.Count > 0 && sign != 0 && runSign != 0 && sign != runSign)
                {
                    // Пересечение: точка, где кривая и граница сходятся.
                    (double previousY, double previousA, double previousB) = run[^1];
                    double previousDifference = previousA - previousB;
                    double t = previousDifference / (previousDifference - difference);
                    double crossY = previousY + (y - previousY) * t;
                    double crossX = previousA + (a - previousA) * t;
                    run.Add((crossY, crossX, crossX));
                    Flush();
                    run.Add((crossY, crossX, crossX));
                }

                if (runSign == 0 && sign != 0)
                {
                    runSign = sign;
                }

                run.Add((y, a, b));
            }

            if (row == last)
            {
                break;
            }
        }

        Flush();
        return polygons;
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
}
