using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ScalableP2P
{
    class Zipf
    {
        Random rnd = new Random(10007);
        double normalizationConstant = 0;
        double z;                     // Uniform random number (0 < z < 1)
        double sum_prob;              // Sum of probabilities
        double zipf_value;            // Computed exponential value to be returned
        int i;                     // Loop counter
        public Zipf(double alpha, int n)
        {

            for (i = 1; i <= n; i++)
            {
                normalizationConstant = normalizationConstant + (1.0 / Math.Pow((double)i, alpha));
                
                
            }
            normalizationConstant = 1.0 / normalizationConstant;
            //Console.WriteLine(normalizationConstant);
        }
        public Zipf(double alpha, int n, int seed)
            : this(alpha, n)
        {
            rnd = new Random(seed);
        }
        public double getZipfValue(double alpha, int n)
        {
            // Pull a uniform random number (0 < z < 1)
            z = rnd.NextDouble();
            // Map z to the value
            sum_prob = 0;
            for (i = 1; i <= n; i++)
            {
                sum_prob = sum_prob + normalizationConstant / Math.Pow((double)i, alpha);
                if (sum_prob >= z)
                {
                    zipf_value = i;
                    break;
                }
            }
            // Assert that zipf_value is between 1 and N
            if ((zipf_value < 1) && (zipf_value > n))
                getZipfValue(alpha, n);

            return (zipf_value);
        }
    }

}