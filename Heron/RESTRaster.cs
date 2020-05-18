﻿using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using System.Linq;
using System.Data;
using System.Drawing;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Collections;
using GH_IO;
using GH_IO.Serialization;

using Newtonsoft.Json.Bson;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using Newtonsoft.Json.Serialization;
using OSGeo.OGR;

namespace Heron
{
    public class RESTRaster : HeronRasterPreviewComponent
    {
        //Class Constructor
        public RESTRaster() : base("Get REST Raster", "RESTRaster", "Get raster imagery from ArcGIS REST Services", "GIS REST")
        {

        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for imagery", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Resolution", "resolution", "Maximum resolution for images", GH_ParamAccess.item,1024);
            pManager.AddTextParameter("File Location", "fileLocation", "Folder to place image files", GH_ParamAccess.item, Path.GetTempPath());
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for image file name", GH_ParamAccess.item, "restRaster");
            pManager.AddTextParameter("REST URL", "URL", "ArcGIS REST Service website to query", GH_ParamAccess.item);
            pManager.AddBooleanParameter("run", "get", "Go ahead and download imagery from the Service", GH_ParamAccess.item, false);

            pManager.AddTextParameter("User Spatial Reference System", "userSRS", "Custom SRS", GH_ParamAccess.item,"WGS84");

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("image", "Image", "File location of downloaded image", GH_ParamAccess.tree);
            pManager.AddCurveParameter("imageFrame", "imageFrame", "Bounding box of image for mapping to geometry", GH_ParamAccess.tree);
            pManager.AddTextParameter("RESTQuery", "RESTQuery", "Full text of REST query", GH_ParamAccess.tree);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>("Boundary", boundary);

            int Res = -1;
            DA.GetData<int>("Resolution", ref Res);

            string fileloc = "";
            DA.GetData<string>("File Location", ref fileloc);
            if (!fileloc.EndsWith(@"\")) fileloc = fileloc + @"\";

            string prefix = "";
            DA.GetData<string>("Prefix", ref prefix);

            string URL = "";
            DA.GetData<string>("REST URL", ref URL);

            bool run = false;
            DA.GetData<bool>("run", ref run);

            string userSRStext = "";
            DA.GetData<string>("User Spatial Reference System", ref userSRStext);

            ///GDAL setup
            RESTful.GdalConfiguration.ConfigureOgr();

            ///TODO: implement SetCRS here.
            ///Option to set CRS here to user-defined.  Needs a SetCRS global variable.
            //string userSRStext = "EPSG:4326";

            OSGeo.OSR.SpatialReference userSRS = new OSGeo.OSR.SpatialReference("");
            userSRS.SetFromUserInput(userSRStext);
            int userSRSInt = Int16.Parse(userSRS.GetAuthorityCode(null));

            ///Set transform from input spatial reference to Rhino spatial reference
            OSGeo.OSR.SpatialReference rhinoSRS = new OSGeo.OSR.SpatialReference("");
            rhinoSRS.SetWellKnownGeogCS("WGS84");

            ///This transform moves and scales the points required in going from userSRS to XYZ and vice versa
            Transform userSRSToModelTransform = Heron.Convert.GetUserSRSToModelTransform(userSRS);
            Transform modelToUserSRSTransform = Heron.Convert.GetModelToUserSRSTransform(userSRS);

            OSGeo.OSR.CoordinateTransformation coordTransformRhinoToUser = new OSGeo.OSR.CoordinateTransformation(rhinoSRS, userSRS);
            OSGeo.OSR.CoordinateTransformation coordTransformUserToRhino = new OSGeo.OSR.CoordinateTransformation(userSRS, rhinoSRS);


            GH_Structure<GH_String> mapList = new GH_Structure<GH_String>();
            GH_Structure<GH_String> mapquery = new GH_Structure<GH_String>();
            GH_Structure<GH_Rectangle> imgFrame = new GH_Structure<GH_Rectangle>();

            FileInfo file = new FileInfo(fileloc);
            file.Directory.Create();

            string size = "";
            if (Res != 0)
            {
                size = "&size=" + Res + "%2C" + Res;
            }

            for (int i = 0; i < boundary.Count; i++)
            {

                GH_Path path = new GH_Path(i);

                //Get image frame for given boundary
                BoundingBox imageBox = boundary[i].GetBoundingBox(false);

                Point3d min = Heron.Convert.XYZToWGS(imageBox.Min);
                Point3d max = Heron.Convert.XYZToWGS(imageBox.Max);
                Rectangle3d rect = BBoxToRect(imageBox);

                ///ogr method
                OSGeo.OGR.Geometry minOgr = Heron.Convert.Point3dToOgrPoint(min);
                minOgr.Transform(coordTransformRhinoToUser);

                OSGeo.OGR.Geometry maxOgr = Heron.Convert.Point3dToOgrPoint(max);
                maxOgr.Transform(coordTransformRhinoToUser);

                //Query the REST service
                string restquery = URL +
                  ///legacy method for creating bounding box string
                  //"bbox=" + Heron.Convert.ConvertLat(min.X, 3857) + "%2C" + Heron.Convert.ConvertLon(min.Y, 3857) + "%2C" + Heron.Convert.ConvertLat(max.X, 3857) + "%2C" + Heron.Convert.ConvertLon(max.Y, 3857) +

                  ///ogr method for creating bounding box string
                  "bbox=" + minOgr.GetX(0) + "%2C" + minOgr.GetY(0) + "%2C" + maxOgr.GetX(0) + "%2C" + maxOgr.GetY(0) +

                  "&bboxSR=" + userSRSInt +
                  size + //"&layers=&layerdefs=" +
                  "&imageSR=" + userSRSInt + //"&transparent=false&dpi=&time=&layerTimeOptions=" +
                  "&format=jpg&f=json";

                mapquery.Append(new GH_String(restquery), path);

                string result = "";

                if (run)
                {

                    ///get extent of image from arcgis rest service as JSON
                    result = Heron.Convert.HttpToJson(restquery);
                    JObject jObj = JsonConvert.DeserializeObject<JObject>(result);
                    Point3d extMin = new Point3d((double) jObj["extent"]["xmin"], (double) jObj["extent"]["ymin"], 0);
                    Point3d extMax = new Point3d((double) jObj["extent"]["xmax"], (double) jObj["extent"]["ymax"], 0);

                    ///convert and transform extents to points
                    OSGeo.OGR.Geometry extOgrMin = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPointZM);
                    extOgrMin.AddPoint((double)jObj["extent"]["xmin"], (double)jObj["extent"]["ymin"], 0.0);
                    extOgrMin.Transform(coordTransformUserToRhino);
                    Point3d ogrPtMin = Heron.Convert.OgrPointToPoint3d(extOgrMin);

                    OSGeo.OGR.Geometry extOgrMax = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPointZM);
                    extOgrMax.AddPoint((double)jObj["extent"]["xmax"], (double)jObj["extent"]["ymax"], 0.0);
                    extOgrMax.Transform(coordTransformUserToRhino);
                    Point3d ogrPtMax = Heron.Convert.OgrPointToPoint3d(extOgrMax);

                    ///if SRS is geographic (ie WGS84) use Rhino's internal projection
                    ///this is still buggy as it doesn't work with other geographic systems like NAD27
                    if ((userSRS.IsProjected() == 0) && (userSRS.IsLocal() == 0))
                    {
                        rect = new Rectangle3d(Plane.WorldXY, Heron.Convert.WGSToXYZ(extMin), Heron.Convert.WGSToXYZ(extMax));
                    }
                    else
                    {
                        //rect = new Rectangle3d(Plane.WorldXY, Heron.Convert.UserSRSToXYZ(extMin, userSRSToModel), Heron.Convert.UserSRSToXYZ(extMax, userSRSToModel));
                        rect = new Rectangle3d(Plane.WorldXY, userSRSToModelTransform * extMin, userSRSToModelTransform * extMax);
                    }


                    ///download image from source
                    string imageQuery = jObj["href"].ToString();
                    System.Net.WebClient webClient = new System.Net.WebClient();
                    webClient.DownloadFile(imageQuery, fileloc + prefix + "_" + i + ".jpg");
                    webClient.Dispose();

                }
                var bitmapPath = fileloc + prefix + "_" + i + ".jpg";
                mapList.Append(new GH_String(bitmapPath), path);

                imgFrame.Append(new GH_Rectangle(rect), path);
                AddPreviewItem(bitmapPath, rect);
            }

            DA.SetDataTree(0, mapList);
            DA.SetDataTree(1, imgFrame);
            DA.SetDataTree(2, mapquery);


        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.raster;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{EB41AAA3-C9DA-42DE-8B58-D4A1CBDADCC8}"); }
        }
    }
}
