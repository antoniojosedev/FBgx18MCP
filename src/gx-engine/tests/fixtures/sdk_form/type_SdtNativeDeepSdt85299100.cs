/*
				   File: type_SdtNativeDeepSdt85299100
			Description: NativeDeepSdt85299100
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
	[XmlRoot(ElementName="NativeDeepSdt85299100")]
	[XmlType(TypeName="NativeDeepSdt85299100" , Namespace="KBTeste" )]
	[Serializable]
	public class SdtNativeDeepSdt85299100 : GxUserType
	{
		public SdtNativeDeepSdt85299100( )
		{
			/* Constructor for serialization */
			gxTv_SdtNativeDeepSdt85299100_Rootvalue = "";

		}

		public SdtNativeDeepSdt85299100(IGxContext context)
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
			AddObjectProperty("RootValue", gxTpr_Rootvalue, false);

			if (gxTv_SdtNativeDeepSdt85299100_Child != null)
			{
				AddObjectProperty("Child", gxTv_SdtNativeDeepSdt85299100_Child, false);
			}
			return;
		}
		#endregion

		#region Properties

		[SoapElement(ElementName="RootValue")]
		[XmlElement(ElementName="RootValue")]
		public string gxTpr_Rootvalue
		{
			get {
				return gxTv_SdtNativeDeepSdt85299100_Rootvalue; 
			}
			set {
				gxTv_SdtNativeDeepSdt85299100_Rootvalue = value;
				SetDirty("Rootvalue");
			}
		}



		[SoapElement(ElementName="Child" )]
		[XmlElement(ElementName="Child" )]
		public SdtNativeDeepSdt85299100_Child gxTpr_Child
		{
			get {
				if ( gxTv_SdtNativeDeepSdt85299100_Child == null )
				{
					gxTv_SdtNativeDeepSdt85299100_Child = new SdtNativeDeepSdt85299100_Child(context);
				}
				gxTv_SdtNativeDeepSdt85299100_Child_N = false;
				return gxTv_SdtNativeDeepSdt85299100_Child;
			}
			set {
				gxTv_SdtNativeDeepSdt85299100_Child_N = false;
				gxTv_SdtNativeDeepSdt85299100_Child = value;
				SetDirty("Child");
			}

		}

		public void gxTv_SdtNativeDeepSdt85299100_Child_SetNull()
		{
			gxTv_SdtNativeDeepSdt85299100_Child_N = true;
			gxTv_SdtNativeDeepSdt85299100_Child = null;
		}

		public bool gxTv_SdtNativeDeepSdt85299100_Child_IsNull()
		{
			return gxTv_SdtNativeDeepSdt85299100_Child == null;
		}
		public bool ShouldSerializegxTpr_Child_Json()
		{
				return (gxTv_SdtNativeDeepSdt85299100_Child != null && gxTv_SdtNativeDeepSdt85299100_Child.ShouldSerializeSdtJson());

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
			gxTv_SdtNativeDeepSdt85299100_Rootvalue = "";

			gxTv_SdtNativeDeepSdt85299100_Child_N = true;

			return  ;
		}



		#endregion

		#region Declaration

		protected string gxTv_SdtNativeDeepSdt85299100_Rootvalue;
		 
		protected bool gxTv_SdtNativeDeepSdt85299100_Child_N;
		protected SdtNativeDeepSdt85299100_Child gxTv_SdtNativeDeepSdt85299100_Child = null; 



		#endregion
	}
	#region Rest interface
	[GxJsonSerialization("default")]
	[DataContract(Name=@"NativeDeepSdt85299100", Namespace="KBTeste")]
	public class SdtNativeDeepSdt85299100_RESTInterface : GxGenericCollectionItem<SdtNativeDeepSdt85299100>, System.Web.SessionState.IRequiresSessionState
	{
		public SdtNativeDeepSdt85299100_RESTInterface( ) : base()
		{	
		}

		public SdtNativeDeepSdt85299100_RESTInterface( SdtNativeDeepSdt85299100 psdt ) : base(psdt)
		{	
		}

		#region Rest Properties
		[DataMember(Name="RootValue", Order=0)]
		public  string gxTpr_Rootvalue
		{
			get { 
				return sdt.gxTpr_Rootvalue;

			}
			set { 
				 sdt.gxTpr_Rootvalue = value;
			}
		}

		[DataMember(Name="Child", Order=1, EmitDefaultValue=false)]
		public SdtNativeDeepSdt85299100_Child_RESTInterface gxTpr_Child
		{
			get {
				if (sdt.ShouldSerializegxTpr_Child_Json())
					return new SdtNativeDeepSdt85299100_Child_RESTInterface(sdt.gxTpr_Child);
				else
					return null;

			}

			set {
				sdt.gxTpr_Child = value.sdt;
			}

		}


		#endregion

		public SdtNativeDeepSdt85299100 sdt
		{
			get { 
				return (SdtNativeDeepSdt85299100)Sdt;
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
				sdt = new SdtNativeDeepSdt85299100() ;
			}
		}
	}
	#endregion
}