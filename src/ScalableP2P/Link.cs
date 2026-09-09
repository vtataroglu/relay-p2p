using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScalableP2P
{
    class Link
    {
        int id;

        public int Id
        {
            get { return id; }
            set { id = value; }
        }

        Link nextLink;

        public Link NextLink
        {
            get { return nextLink; }
            set { nextLink = value; }
        }
        Node vertexLink;

        public Node VertexLink
        {
            get { return vertexLink; }
            set { vertexLink = value; }
        }


        public Link(int id)
        {
            this.id = id;
            nextLink = null;
        }
    }
}
