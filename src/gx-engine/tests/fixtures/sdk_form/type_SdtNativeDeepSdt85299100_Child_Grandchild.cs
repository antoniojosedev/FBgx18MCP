/*
				   File: type_SdtNativeDeepSdt85299100_Child_Grandchild
			Description: Grandchild
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
	[XmlRoot(ElementName="NativeDeepSdt85299100.Child.Grandchild")]
	[XmlType(TypeName="NativeDeepSdt85299100.Child.Grandchild" , Namespace="KBTeste" )]
	[Serializable]
	public class SdtNativeDeepSdt85299100_Child_Grandchild : GxUserType
	{
		public SdtNativeDeepSdt85299100_Child_Grandchild( )
		{
			/* Constructor for serialization */
		}

		public SdtNativeDeepSdt85299100_Child_Grandchild(IGxContext context)
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
			AddObjectProperty("GrandchildValue", gxTpr_Grandchildvalue, false);

			return;
		}
		#endregion

		#region Properties

		[SoapElement(ElementName="GrandchildValue")]
		[XmlElement(ElementName="GrandchildValue")]
		public int gxTpr_Grandchildvalue
		{
			get {
				return gxTv_SdtNativeDeepSdt85299100_Child_Grandchild_Grandchildvalue; 
			}
			set {
				gxTv_SdtNativeDeepSdt85299100_Child_Grandchild_Grandchildvalue = value;
				SetDirty("Grandchildvalue");
			}
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
			return  ;
		}



		#endregion

		#region Declaration

		protected int gxTv_SdtNativeDeepSdt85299100_Child_Grandchild_Grandchildvalue;
		 


		#endregion
	}
	#region Rest interface
	[GxJsonSerialization("default")]
	[DataContract(Name=@"NativeDeepSdt85299100.Child.Grandchild", Namespace="KBTeste")]
	public class SdtNativeDeepSdt85299100_Child_Grandchild_RESTInterface : GxGenericCollectionItem<SdtNativeDeepSdt85299100_Child_Grandchild>, System.Web.SessionState.IRequiresSessionState
	{
		public SdtNativeDeepSdt85299100_Child_Grandchild_RESTInterface( ) : base()
		{	
		}

		public SdtNativeDeepSdt85299100_Child_Grandchild_RESTInterface( SdtNativeDeepSdt85299100_Child_Grandchild psdt ) : base(psdt)
		{	
		}

		#region Rest Properties
		[DataMember(Name="GrandchildValue", Order=0)]
		public  string gxTpr_Grandchildvalue
		{
			get { 
				return StringUtil.LTrim( StringUtil.Str( (decimal) sdt.gxTpr_Grandchildvalue, 8, 0));

			}
			set { 
				sdt.gxTpr_Grandchildvalue = (int) NumberUtil.Val( value, ".");
			}
		}


		#endregion

		public SdtNativeDeepSdt85299100_Child_Grandchild sdt
		{
			get { 
				return (SdtNativeDeepSdt85299100_Child_Grandchild)Sdt;
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
				sdt = new SdtNativeDeepSdt85299100_Child_Grandchild() ;
			}
		}
	}
	#endregion
}