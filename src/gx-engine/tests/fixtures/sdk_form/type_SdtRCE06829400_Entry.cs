/*
				   File: type_SdtRCE06829400_Entry
			Description: Rows
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
	[XmlRoot(ElementName="RCE06829400.Entry")]
	[XmlType(TypeName="RCE06829400.Entry" , Namespace="KBTeste" )]
	[Serializable]
	public class SdtRCE06829400_Entry : GxUserType
	{
		public SdtRCE06829400_Entry( )
		{
			/* Constructor for serialization */
		}

		public SdtRCE06829400_Entry(IGxContext context)
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
		public short gxTpr_Amount
		{
			get {
				return gxTv_SdtRCE06829400_Entry_Amount; 
			}
			set {
				gxTv_SdtRCE06829400_Entry_Amount = value;
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
			return  ;
		}



		#endregion

		#region Declaration

		protected short gxTv_SdtRCE06829400_Entry_Amount;
		 


		#endregion
	}
	#region Rest interface
	[GxJsonSerialization("wrapped")]
	[DataContract(Name=@"RCE06829400.Entry", Namespace="KBTeste")]
	public class SdtRCE06829400_Entry_RESTInterface : GxGenericCollectionItem<SdtRCE06829400_Entry>, System.Web.SessionState.IRequiresSessionState
	{
		public SdtRCE06829400_Entry_RESTInterface( ) : base()
		{	
		}

		public SdtRCE06829400_Entry_RESTInterface( SdtRCE06829400_Entry psdt ) : base(psdt)
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


		#endregion

		public SdtRCE06829400_Entry sdt
		{
			get { 
				return (SdtRCE06829400_Entry)Sdt;
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
				sdt = new SdtRCE06829400_Entry() ;
			}
		}
	}
	#endregion
}