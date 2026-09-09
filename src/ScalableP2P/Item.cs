using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScalableP2P
{
    class Item
    {
        ArrayList values;

        public ArrayList Values
        {
            get { return values; }
            set { values = value; }
        }

        double popularity;

        public double Popularity
        {
            get { return popularity; }
            set { popularity = value; }
        }

        public Item()
        {
            values = new ArrayList();
            popularity = values.Count;
        }

        public void addItem(object item)
        {
            values.Add(item);
            popularity += Convert.ToDouble(item);
        }

        public double calculatePopularity()//int index removed
        {
            return values.Count;
        }
    }
}