/*
				   File: type_SdtRustFreshSdt091817
			Description: RustFreshSdt091817
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
	[XmlRoot(ElementName="RustFreshSdt091817")]
	[XmlType(TypeName="RustFreshSdt091817" , Namespace="KBTeste" )]
	[Serializable]
	public class SdtRustFreshSdt091817 : GxUserType
	{
		public SdtRustFreshSdt091817( )
		{
			/* Constructor for serialization */
		}

		public SdtRustFreshSdt091817(IGxContext context)
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
				return gxTv_SdtRustFreshSdt091817_Amount; 
			}
			set {
				gxTv_SdtRustFreshSdt091817_Amount = value;
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

		protected short gxTv_SdtRustFreshSdt091817_Amount;
		 


		#endregion
	}
	#region Rest interface
	[GxJsonSerialization("default")]
	[DataContract(Name=@"RustFreshSdt091817", Namespace="KBTeste")]
	public class SdtRustFreshSdt091817_RESTInterface : GxGenericCollectionItem<SdtRustFreshSdt091817>, System.Web.SessionState.IRequiresSessionState
	{
		public SdtRustFreshSdt091817_RESTInterface( ) : base()
		{	
		}

		public SdtRustFreshSdt091817_RESTInterface( SdtRustFreshSdt091817 psdt ) : base(psdt)
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

		public SdtRustFreshSdt091817 sdt
		{
			get { 
				return (SdtRustFreshSdt091817)Sdt;
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
				sdt = new SdtRustFreshSdt091817() ;
			}
		}
	}
	#endregion
}