/*
				   File: type_SdtNativeDeepSdt85299100_Child_NestedCollectionItem
			Description: NestedCollection
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
	[XmlRoot(ElementName="NativeDeepSdt85299100.Child.NestedCollectionItem")]
	[XmlType(TypeName="NativeDeepSdt85299100.Child.NestedCollectionItem" , Namespace="KBTeste" )]
	[Serializable]
	public class SdtNativeDeepSdt85299100_Child_NestedCollectionItem : GxUserType
	{
		public SdtNativeDeepSdt85299100_Child_NestedCollectionItem( )
		{
			/* Constructor for serialization */
			gxTv_SdtNativeDeepSdt85299100_Child_NestedCollectionItem_Nestedvalue = "";

		}

		public SdtNativeDeepSdt85299100_Child_NestedCollectionItem(IGxContext context)
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
			AddObjectProperty("NestedValue", gxTpr_Nestedvalue, false);

			return;
		}
		#endregion

		#region Properties

		[SoapElement(ElementName="NestedValue")]
		[XmlElement(ElementName="NestedValue")]
		public string gxTpr_Nestedvalue
		{
			get {
				return gxTv_SdtNativeDeepSdt85299100_Child_NestedCollectionItem_Nestedvalue; 
			}
			set {
				gxTv_SdtNativeDeepSdt85299100_Child_NestedCollectionItem_Nestedvalue = value;
				SetDirty("Nestedvalue");
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
			gxTv_SdtNativeDeepSdt85299100_Child_NestedCollectionItem_Nestedvalue = "";
			return  ;
		}



		#endregion

		#region Declaration

		protected string gxTv_SdtNativeDeepSdt85299100_Child_NestedCollectionItem_Nestedvalue;
		 


		#endregion
	}
	#region Rest interface
	[GxJsonSerialization("wrapped")]
	[DataContract(Name=@"NativeDeepSdt85299100.Child.NestedCollectionItem", Namespace="KBTeste")]
	public class SdtNativeDeepSdt85299100_Child_NestedCollectionItem_RESTInterface : GxGenericCollectionItem<SdtNativeDeepSdt85299100_Child_NestedCollectionItem>, System.Web.SessionState.IRequiresSessionState
	{
		public SdtNativeDeepSdt85299100_Child_NestedCollectionItem_RESTInterface( ) : base()
		{	
		}

		public SdtNativeDeepSdt85299100_Child_NestedCollectionItem_RESTInterface( SdtNativeDeepSdt85299100_Child_NestedCollectionItem psdt ) : base(psdt)
		{	
		}

		#region Rest Properties
		[DataMember(Name="NestedValue", Order=0)]
		public  string gxTpr_Nestedvalue
		{
			get { 
				return sdt.gxTpr_Nestedvalue;

			}
			set { 
				 sdt.gxTpr_Nestedvalue = value;
			}
		}


		#endregion

		public SdtNativeDeepSdt85299100_Child_NestedCollectionItem sdt
		{
			get { 
				return (SdtNativeDeepSdt85299100_Child_NestedCollectionItem)Sdt;
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
				sdt = new SdtNativeDeepSdt85299100_Child_NestedCollectionItem() ;
			}
		}
	}
	#endregion
}