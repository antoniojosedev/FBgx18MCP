/*
				   File: type_SdtLiveSdtMember60744600
			Description: LiveSdtMember60744600
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
	[XmlRoot(ElementName="LiveSdtMember60744600")]
	[XmlType(TypeName="LiveSdtMember60744600" , Namespace="KBTeste" )]
	[Serializable]
	public class SdtLiveSdtMember60744600 : GxUserType
	{
		public SdtLiveSdtMember60744600( )
		{
			/* Constructor for serialization */
			gxTv_SdtLiveSdtMember60744600_Amount = "";

		}

		public SdtLiveSdtMember60744600(IGxContext context)
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

			return;
		}
		#endregion

		#region Properties

		[SoapElement(ElementName="Amount")]
		[XmlElement(ElementName="Amount")]
		public string gxTpr_Amount
		{
			get {
				return gxTv_SdtLiveSdtMember60744600_Amount; 
			}
			set {
				gxTv_SdtLiveSdtMember60744600_Amount = value;
				SetDirty("Amount");
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
			gxTv_SdtLiveSdtMember60744600_Amount = "";
			return  ;
		}



		#endregion

		#region Declaration

		protected string gxTv_SdtLiveSdtMember60744600_Amount;
		 


		#endregion
	}
	#region Rest interface
	[GxJsonSerialization("default")]
	[DataContract(Name=@"LiveSdtMember60744600", Namespace="KBTeste")]
	public class SdtLiveSdtMember60744600_RESTInterface : GxGenericCollectionItem<SdtLiveSdtMember60744600>, System.Web.SessionState.IRequiresSessionState
	{
		public SdtLiveSdtMember60744600_RESTInterface( ) : base()
		{	
		}

		public SdtLiveSdtMember60744600_RESTInterface( SdtLiveSdtMember60744600 psdt ) : base(psdt)
		{	
		}

		#region Rest Properties
		[DataMember(Name="Amount", Order=0)]
		public  string gxTpr_Amount
		{
			get { 
				return sdt.gxTpr_Amount;

			}
			set { 
				 sdt.gxTpr_Amount = value;
			}
		}


		#endregion

		public SdtLiveSdtMember60744600 sdt
		{
			get { 
				return (SdtLiveSdtMember60744600)Sdt;
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
				sdt = new SdtLiveSdtMember60744600() ;
			}
		}
	}
	#endregion
}