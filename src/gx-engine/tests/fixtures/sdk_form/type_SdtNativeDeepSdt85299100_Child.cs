/*
				   File: type_SdtNativeDeepSdt85299100_Child
			Description: Child
				 Author: Nemo 🐠 for C# (.NET) version 18.0.10.184260
		   Program type: Callable routine
			  Main DBMS: 
*/
using System;
using System.Collections;
using GeneXus.Utils;
using GeneXus.Resources;
using GeneXus.Application;
using GeneXus.Metadata;
using GeneXus.Cryptography;
using GeneXus.Encryption;
using GeneXus.Http.Client;
using GeneXus.Http.Server;
using System.Reflection;
using System.Xml.Serialization;
using System.Runtime.Serialization;


namespace GeneXus.Programs
{
	[XmlRoot(ElementName="NativeDeepSdt85299100.Child")]
	[XmlType(TypeName="NativeDeepSdt85299100.Child" , Namespace="KBTeste" )]
	[Serializable]
	public class SdtNativeDeepSdt85299100_Child : GxUserType
	{
		public SdtNativeDeepSdt85299100_Child( )
		{
			/* Constructor for serialization */
			gxTv_SdtNativeDeepSdt85299100_Child_Childvalue = "";

		}

		public SdtNativeDeepSdt85299100_Child(IGxContext context)
		{
			this.context = context;	
			initialize();
		}

		#region Json
		private static Hashtable mapper;
		public override string JsonMap(string value)
		{
			if (mapper == null)
			{
				mapper = new Hashtable();
			}
			return (string)mapper[value]; ;
		}

		public override void ToJSON()
		{
			ToJSON(true) ;
			return;
		}

		public override void ToJSON(bool includeState)
		{
			AddObjectProperty("ChildValue", gxTpr_Childvalue, false);

			if (gxTv_SdtNativeDeepSdt85299100_Child_Grandchild != null)
			{
				AddObjectProperty("Grandchild", gxTv_SdtNativeDeepSdt85299100_Child_Grandchild, false);
			}
			if (gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection != null)
			{
				AddObjectProperty("NestedCollection", gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection, false);
			}
			return;
		}
		#endregion

		#region Properties

		[SoapElement(ElementName="ChildValue")]
		[XmlElement(ElementName="ChildValue")]
		public string gxTpr_Childvalue
		{
			get {
				return gxTv_SdtNativeDeepSdt85299100_Child_Childvalue; 
			}
			set {
				gxTv_SdtNativeDeepSdt85299100_Child_Childvalue = value;
				SetDirty("Childvalue");
			}
		}



		[SoapElement(ElementName="Grandchild" )]
		[XmlElement(ElementName="Grandchild" )]
		public SdtNativeDeepSdt85299100_Child_Grandchild gxTpr_Grandchild
		{
			get {
				if ( gxTv_SdtNativeDeepSdt85299100_Child_Grandchild == null )
				{
					gxTv_SdtNativeDeepSdt85299100_Child_Grandchild = new SdtNativeDeepSdt85299100_Child_Grandchild(context);
				}
				gxTv_SdtNativeDeepSdt85299100_Child_Grandchild_N = false;
				return gxTv_SdtNativeDeepSdt85299100_Child_Grandchild;
			}
			set {
				gxTv_SdtNativeDeepSdt85299100_Child_Grandchild_N = false;
				gxTv_SdtNativeDeepSdt85299100_Child_Grandchild = value;
				SetDirty("Grandchild");
			}

		}

		public void gxTv_SdtNativeDeepSdt85299100_Child_Grandchild_SetNull()
		{
			gxTv_SdtNativeDeepSdt85299100_Child_Grandchild_N = true;
			gxTv_SdtNativeDeepSdt85299100_Child_Grandchild = null;
		}

		public bool gxTv_SdtNativeDeepSdt85299100_Child_Grandchild_IsNull()
		{
			return gxTv_SdtNativeDeepSdt85299100_Child_Grandchild == null;
		}
		public bool ShouldSerializegxTpr_Grandchild_Json()
		{
				return (gxTv_SdtNativeDeepSdt85299100_Child_Grandchild != null && gxTv_SdtNativeDeepSdt85299100_Child_Grandchild.ShouldSerializeSdtJson());

		}



		[SoapElement(ElementName="NestedCollection" )]
		[XmlArray(ElementName="NestedCollection"  )]
		[XmlArrayItemAttribute(ElementName="NestedCollectionItem" , IsNullable=false )]
		public GXBaseCollection<SdtNativeDeepSdt85299100_Child_NestedCollectionItem> gxTpr_Nestedcollection
		{
			get {
				if ( gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection == null )
				{
					gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection = new GXBaseCollection<SdtNativeDeepSdt85299100_Child_NestedCollectionItem>( context, "NativeDeepSdt85299100.Child.NestedCollectionItem", "");
				}
				return gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection;
			}
			set {
				gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection_N = false;
				gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection = value;
				SetDirty("Nestedcollection");
			}
		}

		public void gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection_SetNull()
		{
			gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection_N = true;
			gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection = null;
		}

		public bool gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection_IsNull()
		{
			return gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection == null;
		}
		public bool ShouldSerializegxTpr_Nestedcollection_GxSimpleCollection_Json()
		{
			return gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection != null && gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection.Count > 0;

		}


		public override bool ShouldSerializeSdtJson()
		{
			return true;
		}



		#endregion

		#region Static Type Properties

		[XmlIgnore]
		private static GXTypeInfo _typeProps;
		protected override GXTypeInfo TypeInfo { get { return _typeProps; } set { _typeProps = value; } }

		#endregion

		#region Initialization

		public void initialize( )
		{
			gxTv_SdtNativeDeepSdt85299100_Child_Childvalue = "";

			gxTv_SdtNativeDeepSdt85299100_Child_Grandchild_N = true;


			gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection_N = true;

			return  ;
		}



		#endregion

		#region Declaration

		protected string gxTv_SdtNativeDeepSdt85299100_Child_Childvalue;
		 
		protected bool gxTv_SdtNativeDeepSdt85299100_Child_Grandchild_N;
		protected SdtNativeDeepSdt85299100_Child_Grandchild gxTv_SdtNativeDeepSdt85299100_Child_Grandchild = null; 

		protected bool gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection_N;
		protected GXBaseCollection<SdtNativeDeepSdt85299100_Child_NestedCollectionItem> gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection = null; 



		#endregion
	}
	#region Rest interface
	[GxJsonSerialization("default")]
	[DataContract(Name=@"NativeDeepSdt85299100.Child", Namespace="KBTeste")]
	public class SdtNativeDeepSdt85299100_Child_RESTInterface : GxGenericCollectionItem<SdtNativeDeepSdt85299100_Child>, System.Web.SessionState.IRequiresSessionState
	{
		public SdtNativeDeepSdt85299100_Child_RESTInterface( ) : base()
		{	
		}

		public SdtNativeDeepSdt85299100_Child_RESTInterface( SdtNativeDeepSdt85299100_Child psdt ) : base(psdt)
		{	
		}

		#region Rest Properties
		[DataMember(Name="ChildValue", Order=0)]
		public  string gxTpr_Childvalue
		{
			get { 
				return sdt.gxTpr_Childvalue;

			}
			set { 
				 sdt.gxTpr_Childvalue = value;
			}
		}

		[DataMember(Name="Grandchild", Order=1, EmitDefaultValue=false)]
		public SdtNativeDeepSdt85299100_Child_Grandchild_RESTInterface gxTpr_Grandchild
		{
			get {
				if (sdt.ShouldSerializegxTpr_Grandchild_Json())
					return new SdtNativeDeepSdt85299100_Child_Grandchild_RESTInterface(sdt.gxTpr_Grandchild);
				else
					return null;

			}

			set {
				sdt.gxTpr_Grandchild = value.sdt;
			}

		}

		[DataMember(Name="NestedCollection", Order=2, EmitDefaultValue=false)]
		public GxGenericCollection<SdtNativeDeepSdt85299100_Child_NestedCollectionItem_RESTInterface> gxTpr_Nestedcollection
		{
			get {
				if (sdt.ShouldSerializegxTpr_Nestedcollection_GxSimpleCollection_Json())
					return new GxGenericCollection<SdtNativeDeepSdt85299100_Child_NestedCollectionItem_RESTInterface>(sdt.gxTpr_Nestedcollection);
				else
					return null;

			}
			set {
				value.LoadCollection(sdt.gxTpr_Nestedcollection);
			}
		}


		#endregion

		public SdtNativeDeepSdt85299100_Child sdt
		{
			get { 
				return (SdtNativeDeepSdt85299100_Child)Sdt;
			}
			set { 
				Sdt = value;
			}
		}

		[OnDeserializing]
		void checkSdt( StreamingContext ctx )
		{
			if ( sdt == null )
			{
				sdt = new SdtNativeDeepSdt85299100_Child() ;
			}
		}
	}
	#endregion
}