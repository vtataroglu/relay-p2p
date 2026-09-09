using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ScalableP2P
{
    // Time varying query workload: demand for TBP (to-be-popular) content
    // rises linearly from TStart up to QMax at TPeak. Other queries arrive
    // at a rate that grows with item popularity, as in the 2016 model: the
    // rank r drawn from Zipf (1 = most frequent) maps to popularity
    // v = 101 - r, so the most popular item (v = 100) is queried most often.
    class TrendModel
    {
        // multiple TBP support: content k carries TbpVals[k] and has its
        // own ramp; the single content default matches the old behavior
        public int[] TbpVals = new int[] { 1000 };
        public int[] TStarts = new int[] { 2500 };
        public int[] TPeaks = new int[] { 7000 };
        public double[] QMaxes = new double[] { 0.2 };
        // demand shape: 0 linear ramp, 1 step (flash crowd), 2 exponential,
        // 3 pulse (rise then decay), 4 Bass diffusion (RATE of the S curve)
        public int Shape = 0;

        // ---- Shape 4: Bass diffusion -------------------------------------
        // Q(t) = q_max * 4z/(1+z)^2,  z = (q/p) e^{-beta t},  beta = p+q
        // Q(t_inf) = q_max and t_inf = ln(q/p)/beta; q > p is REQUIRED
        // (otherwise there is no inflection point; no silent clipping, this
        // raises a hard error). Bass time is continuous while the simulator
        // counts steps: one Bass unit is StepsPerBassUnit steps, and
        // beta_w = beta * W / StepsPerBassUnit.
        public int StepsPerBassUnit = 200;
        public double[] Pcoef = new double[] { 0.01 };
        public double[] Qcoef = new double[] { 0.40 };

        // t_inf expressed in simulation steps (for reporting)
        public double InflectionStep(int k)
        {
            return TStarts[k] + StepsPerBassUnit * Math.Log(Qcoef[k] / Pcoef[k]) / (Pcoef[k] + Qcoef[k]);
        }

        public void ValidateBass()
        {
            for (int k = 0; k < Pcoef.Length; k++)
            {
                if (!(Qcoef[k] > Pcoef[k]))
                    throw new ArgumentException("Bass requires q > p (no interior inflection otherwise): p=" +
                                                Pcoef[k] + " q=" + Qcoef[k]);
                if (!(Pcoef[k] > 0))
                    throw new ArgumentException("Bass requires p > 0: p=" + Pcoef[k]);
            }
        }

        Zipf zipf = new Zipf(0.3, 100);
        Random rnd;

        public TrendModel(int seed)
        {
            rnd = new Random(seed);
        }

        public int TbpVal
        {
            get { return TbpVals[0]; }
        }

        public double queryProbability(int t, int k)
        {
            if (t < TStarts[k]) return 0;
            if (Shape == 1) return QMaxes[k];
            if (Shape == 2)
            {
                double tau = (TPeaks[k] - TStarts[k]) / 3.0;
                return QMaxes[k] * (1 - Math.Exp(-(t - TStarts[k]) / tau));
            }
            if (Shape == 3)
            {
                int dur = TPeaks[k] - TStarts[k];
                int tEnd = TPeaks[k] + dur;
                if (t >= tEnd) return 0;
                if (t < TPeaks[k])
                    return QMaxes[k] * (t - TStarts[k]) / (double)dur;
                return QMaxes[k] * (tEnd - t) / (double)dur;
            }
            if (Shape == 4)
            {
                double tb = (t - TStarts[k]) / (double)StepsPerBassUnit;
                double beta = Pcoef[k] + Qcoef[k];
                double z = (Qcoef[k] / Pcoef[k]) * Math.Exp(-beta * tb);
                return QMaxes[k] * 4.0 * z / ((1.0 + z) * (1.0 + z));
            }
            if (t >= TPeaks[k]) return QMaxes[k];
            return QMaxes[k] * (t - TStarts[k]) / (double)(TPeaks[k] - TStarts[k]);
        }

        // if the shares sum past 1 the later TBP contents become
        // unreachable and background traffic silently disappears. Warn once
        // without changing behavior, so nothing degrades silently.
        bool overUnityWarned = false;

        public int getQueryValue(int t)
        {
            double u = rnd.NextDouble();
            double acc = 0;
            for (int k = 0; k < TbpVals.Length; k++)
            {
                acc += queryProbability(t, k);
                if (u < acc) return TbpVals[k];
            }
            if (acc > 1.0 && !overUnityWarned)
            {
                overUnityWarned = true;
                Console.WriteLine("WARN TrendModel: sum of TBP query shares = " +
                    acc.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) +
                    " > 1 at t=" + t + "; later items unreachable, background traffic suppressed.");
            }
            return 101 - (int)zipf.getZipfValue(0.3, 100);
        }
    }
}
