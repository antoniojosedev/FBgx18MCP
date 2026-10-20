/*
				   File: type_SdtRSC79223900
			Description: RSC79223900
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
	[XmlRoot(ElementName="RSC79223900")]
	[XmlType(TypeName="RSC79223900" , Namespace="KBTeste" )]
	[Serializable]
	public class SdtRSC79223900 : GxUserType
	{
		public SdtRSC79223900( )
		{
			/* Constructor for serialization */
			gxTv_SdtRSC79223900_Label = "";

			gxTv_SdtRSC79223900_Fixed = "";

			gxTv_SdtRSC79223900_Stamp = (DateTime)(DateTime.MinValue);

		}

		public SdtRSC79223900(IGxContext context)
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


			AddObjectProperty("Flag", gxTpr_Flag, false);


			sDateCnv = "";
			sNumToPad = StringUtil.Trim(StringUtil.Str((decimal)(DateTimeUtil.Year(gxTpr_Day)), 10, 0));
			sDateCnv = sDateCnv + StringUtil.Substring("0000", 1, 4-StringUtil.Len(sNumToPad)) + sNumToPad;
			sDateCnv = sDateCnv + "-";
			sNumToPad = StringUtil.Trim(StringUtil.Str((decimal)(DateTimeUtil.Month(gxTpr_Day)), 10, 0));
			sDateCnv = sDateCnv + StringUtil.Substring("00", 1, 2-StringUtil.Len(sNumToPad)) + sNumToPad;
			sDateCnv = sDateCnv + "-";
			sNumToPad = StringUtil.Trim(StringUtil.Str((decimal)(DateTimeUtil.Day(gxTpr_Day)), 10, 0));
			sDateCnv = sDateCnv + StringUtil.Substring("00", 1, 2-StringUtil.Len(sNumToPad)) + sNumToPad;
			AddObjectProperty("Day", sDateCnv, false);



			datetime_STZ = gxTpr_Stamp;
			sDateCnv = "";
			sNumToPad = StringUtil.Trim(StringUtil.Str((decimal)(DateTimeUtil.Year(datetime_STZ)), 10, 0));
			sDateCnv = sDateCnv + StringUtil.Substring("0000", 1, 4-StringUtil.Len( sNumToPad)) + sNumToPad;
			sDateCnv = sDateCnv + "-";
			sNumToPad = StringUtil.Trim( StringUtil.Str((decimal)(DateTimeUtil.Month(datetime_STZ)), 10, 0));
			sDateCnv = sDateCnv + StringUtil.Substring("00", 1, 2-StringUtil.Len(sNumToPad)) + sNumToPad;
			sDateCnv = sDateCnv + "-";
			sNumToPad = StringUtil.Trim(StringUtil.Str((decimal)(DateTimeUtil.Day(datetime_STZ)), 10, 0));
			sDateCnv = sDateCnv + StringUtil.Substring("00", 1, 2-StringUtil.Len(sNumToPad)) + sNumToPad;
			sDateCnv = sDateCnv + "T";
			sNumToPad = StringUtil.Trim(StringUtil.Str((decimal)(DateTimeUtil.Hour(datetime_STZ)), 10, 0));
			sDateCnv = sDateCnv + StringUtil.Substring("00", 1, 2-StringUtil.Len(sNumToPad)) + sNumToPad;
			sDateCnv = sDateCnv + ":";
			sNumToPad = StringUtil.Trim(StringUtil.Str((decimal)(DateTimeUtil.Minute(datetime_STZ)), 10, 0));
			sDateCnv = sDateCnv + StringUtil.Substring("00", 1, 2-StringUtil.Len(sNumToPad)) + sNumToPad;
			sDateCnv = sDateCnv + ":";
			sNumToPad = StringUtil.Trim(StringUtil.Str((decimal)(DateTimeUtil.Second(datetime_STZ)), 10, 0));
			sDateCnv = sDateCnv + StringUtil.Substring("00", 1, 2-StringUtil.Len(sNumToPad)) + sNumToPad;
			AddObjectProperty("Stamp", sDateCnv, false);


			return;
		}
		#endregion

		#region Properties

		[SoapElement(ElementName="Amount")]
		[XmlElement(ElementName="Amount")]
		public short gxTpr_Amount
		{
			get {
				return gxTv_SdtRSC79223900_Amount; 
			}
			set {
				gxTv_SdtRSC79223900_Amount = value;
				SetDirty("Amount");
			}
		}




		[SoapElement(ElementName="Label")]
		[XmlElement(ElementName="Label")]
		public string gxTpr_Label
		{
			get {
				return gxTv_SdtRSC79223900_Label; 
			}
			set {
				gxTv_SdtRSC79223900_Label = value;
				SetDirty("Label");
			}
		}




		[SoapElement(ElementName="Fixed")]
		[XmlElement(ElementName="Fixed")]
		public string gxTpr_Fixed
		{
			get {
				return gxTv_SdtRSC79223900_Fixed; 
			}
			set {
				gxTv_SdtRSC79223900_Fixed = value;
				SetDirty("Fixed");
			}
		}




		[SoapElement(ElementName="Flag")]
		[XmlElement(ElementName="Flag")]
		public bool gxTpr_Flag
		{
			get {
				return gxTv_SdtRSC79223900_Flag; 
			}
			set {
				gxTv_SdtRSC79223900_Flag = value;
				SetDirty("Flag");
			}
		}



		[SoapElement(ElementName="Day")]
		[XmlElement(ElementName="Day" , IsNullable=true)]
		public string gxTpr_Day_Nullable
		{
			get {
				if ( gxTv_SdtRSC79223900_Day == DateTime.MinValue)
					return null;
				return new GxDateString(gxTv_SdtRSC79223900_Day).value ;
			}
			set {
				gxTv_SdtRSC79223900_Day = DateTimeUtil.CToD2(value);
			}
		}

		[XmlIgnore]
		public DateTime gxTpr_Day
		{
			get {
				return gxTv_SdtRSC79223900_Day; 
			}
			set {
				gxTv_SdtRSC79223900_Day = value;
				SetDirty("Day");
			}
		}


		[SoapElement(ElementName="Stamp")]
		[XmlElement(ElementName="Stamp" , IsNullable=true)]
		public string gxTpr_Stamp_Nullable
		{
			get {
				if ( gxTv_SdtRSC79223900_Stamp == DateTime.MinValue)
					return null;
				return new GxDatetimeString(gxTv_SdtRSC79223900_Stamp).value ;
			}
			set {
				gxTv_SdtRSC79223900_Stamp = DateTimeUtil.CToD2(value);
			}
		}

		[XmlIgnore]
		public DateTime gxTpr_Stamp
		{
			get {
				return gxTv_SdtRSC79223900_Stamp; 
			}
			set {
				gxTv_SdtRSC79223900_Stamp = value;
				SetDirty("Stamp");
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
			gxTv_SdtRSC79223900_Label = "";
			gxTv_SdtRSC79223900_Fixed = "";


			gxTv_SdtRSC79223900_Stamp = (DateTime)(DateTime.MinValue);
			datetime_STZ = (DateTime)(DateTime.MinValue);
			sDateCnv = "";
			sNumToPad = "";
			return  ;
		}



		#endregion

		#region Declaration

		protected string sDateCnv ;
		protected string sNumToPad ;
		protected DateTime datetime_STZ ;

		protected short gxTv_SdtRSC79223900_Amount;
		 

		protected string gxTv_SdtRSC79223900_Label;
		 

		protected string gxTv_SdtRSC79223900_Fixed;
		 

		protected bool gxTv_SdtRSC79223900_Flag;
		 

		protected DateTime gxTv_SdtRSC79223900_Day;
		 

		protected DateTime gxTv_SdtRSC79223900_Stamp;
		 


		#endregion
	}
	#region Rest interface
	[GxJsonSerialization("default")]
	[DataContract(Name=@"RSC79223900", Namespace="KBTeste")]
	public class SdtRSC79223900_RESTInterface : GxGenericCollectionItem<SdtRSC79223900>, System.Web.SessionState.IRequiresSessionState
	{
		public SdtRSC79223900_RESTInterface( ) : base()
		{	
		}

		public SdtRSC79223900_RESTInterface( SdtRSC79223900 psdt ) : base(psdt)
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

		[DataMember(Name="Flag", Order=3)]
		public bool gxTpr_Flag
		{
			get { 
				return sdt.gxTpr_Flag;

			}
			set { 
				sdt.gxTpr_Flag = value;
			}
		}

		[DataMember(Name="Day", Order=4)]
		public  string gxTpr_Day
		{
			get { 
				return DateTimeUtil.DToC2( sdt.gxTpr_Day);

			}
			set { 
				sdt.gxTpr_Day = DateTimeUtil.CToD2(value);
			}
		}

		[DataMember(Name="Stamp", Order=5)]
		public  string gxTpr_Stamp
		{
			get { 
				return DateTimeUtil.TToC2( sdt.gxTpr_Stamp,context);

			}
			set { 
				sdt.gxTpr_Stamp = DateTimeUtil.CToT2(value,context);
			}
		}


		#endregion

		public SdtRSC79223900 sdt
		{
			get { 
				return (SdtRSC79223900)Sdt;
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
				sdt = new SdtRSC79223900() ;
			}
		}
	}
	#endregion
}