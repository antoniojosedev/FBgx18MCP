/*
				   File: type_SdtRST54161700
			Description: RST54161700
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
	[XmlRoot(ElementName="RST54161700")]
	[XmlType(TypeName="RST54161700" , Namespace="KBTeste" )]
	[Serializable]
	public class SdtRST54161700 : GxUserType
	{
		public SdtRST54161700( )
		{
			/* Constructor for serialization */
			gxTv_SdtRST54161700_Label = "";

			gxTv_SdtRST54161700_Fixed = "";

		}

		public SdtRST54161700(IGxContext context)
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
			AddObjectProperty("Amount", gxTpr_Amount, false);


			AddObjectProperty("Label", gxTpr_Label, false);


			AddObjectProperty("Fixed", gxTpr_Fixed, false);

			return;
		}
		#endregion

		#region Properties

		[SoapElement(ElementName="Amount")]
		[XmlElement(ElementName="Amount")]
		public short gxTpr_Amount
		{
			get {
				return gxTv_SdtRST54161700_Amount; 
			}
			set {
				gxTv_SdtRST54161700_Amount = value;
				SetDirty("Amount");
			}
		}




		[SoapElement(ElementName="Label")]
		[XmlElement(ElementName="Label")]
		public string gxTpr_Label
		{
			get {
				return gxTv_SdtRST54161700_Label; 
			}
			set {
				gxTv_SdtRST54161700_Label = value;
				SetDirty("Label");
			}
		}




		[SoapElement(ElementName="Fixed")]
		[XmlElement(ElementName="Fixed")]
		public string gxTpr_Fixed
		{
			get {
				return gxTv_SdtRST54161700_Fixed; 
			}
			set {
				gxTv_SdtRST54161700_Fixed = value;
				SetDirty("Fixed");
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
			gxTv_SdtRST54161700_Label = "";
			gxTv_SdtRST54161700_Fixed = "";
			return  ;
		}



		#endregion

		#region Declaration

		protected short gxTv_SdtRST54161700_Amount;
		 

		protected string gxTv_SdtRST54161700_Label;
		 

		protected string gxTv_SdtRST54161700_Fixed;
		 


		#endregion
	}
	#region Rest interface
	[GxJsonSerialization("default")]
	[DataContract(Name=@"RST54161700", Namespace="KBTeste")]
	public class SdtRST54161700_RESTInterface : GxGenericCollectionItem<SdtRST54161700>, System.Web.SessionState.IRequiresSessionState
	{
		public SdtRST54161700_RESTInterface( ) : base()
		{	
		}

		public SdtRST54161700_RESTInterface( SdtRST54161700 psdt ) : base(psdt)
		{	
		}

		#region Rest Properties
		[DataMember(Name="Amount", Order=0)]
		public short gxTpr_Amount
		{
			get { 
				return sdt.gxTpr_Amount;

			}
			set { 
				sdt.gxTpr_Amount = value;
			}
		}

		[DataMember(Name="Label", Order=1)]
		public  string gxTpr_Label
		{
			get { 
				return sdt.gxTpr_Label;

			}
			set { 
				 sdt.gxTpr_Label = value;
			}
		}

		[DataMember(Name="Fixed", Order=2)]
		public  string gxTpr_Fixed
		{
			get { 
				return StringUtil.RTrim( sdt.gxTpr_Fixed);

			}
			set { 
				 sdt.gxTpr_Fixed = value;
			}
		}


		#endregion

		public SdtRST54161700 sdt
		{
			get { 
				return (SdtRST54161700)Sdt;
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
				sdt = new SdtRST54161700() ;
			}
		}
	}
	#endregion
}