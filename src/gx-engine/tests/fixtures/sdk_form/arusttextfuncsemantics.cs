using System;
using System.Collections;
using GeneXus.Utils;
using GeneXus.Resources;
using GeneXus.Application;
using GeneXus.Metadata;
using GeneXus.Cryptography;
using com.genexus;
using GeneXus.Data.ADO;
using GeneXus.Data.NTier;
using GeneXus.Data.NTier.ADO;
using GeneXus.WebControls;
using GeneXus.Http;
using GeneXus.Procedure;
using GeneXus.XML;
using GeneXus.Search;
using GeneXus.Encryption;
using GeneXus.Http.Client;
using System.Threading;
using System.Xml.Serialization;
using System.Runtime.Serialization;
namespace GeneXus.Programs {
   public class arusttextfuncsemantics : GXProcedure
   {
      public static int Main( string[] args )
      {
         return new arusttextfuncsemantics().MainImpl(args); ;
      }

      public int executeCmdLine( string[] args )
      {
         return ExecuteCmdLine(args); ;
      }

      protected override int ExecuteCmdLine( string[] args )
      {
         execute();
         return GX.GXRuntime.ExitCode ;
      }

      public arusttextfuncsemantics( )
      {
         context = new GxContext(  );
         DataStoreUtil.LoadDataStores( context);
         IsMain = true;
         context.SetDefaultTheme("KBTeste", true);
      }

      public arusttextfuncsemantics( IGxContext context )
      {
         this.context = context;
         IsMain = false;
      }

      public void execute( )
      {
         initialize();
         ExecuteImpl();
      }

      public void executeSubmit( )
      {
         SubmitImpl();
      }

      protected override void ExecutePrivate( )
      {
         /* GeneXus formulas */
         /* Output device settings */
         AV8C4 = "a b";
         context.StatusMessage( "sp_emb_len="+StringUtil.Trim( StringUtil.Str( (decimal)(StringUtil.Len( AV8C4)), 10, 0)) );
         context.StatusMessage( "sp_emb_sub31=["+StringUtil.Substring( AV8C4, 3, 1)+"]" );
         context.StatusMessage( "sp_emb_sub22=["+StringUtil.Substring( AV8C4, 2, 2)+"]" );
         AV8C4 = " ab";
         context.StatusMessage( "sp_lead_len="+StringUtil.Trim( StringUtil.Str( (decimal)(StringUtil.Len( AV8C4)), 10, 0)) );
         context.StatusMessage( "sp_lead_sub11=["+StringUtil.Substring( AV8C4, 1, 1)+"]" );
         context.StatusMessage( "sp_lead_sub22=["+StringUtil.Substring( AV8C4, 2, 2)+"]" );
         AV9V20 = "ab ";
         context.StatusMessage( "sp_trail_len="+StringUtil.Trim( StringUtil.Str( (decimal)(StringUtil.Len( AV9V20)), 10, 0)) );
         context.StatusMessage( "sp_trail_sub12=["+StringUtil.Substring( AV9V20, 1, 2)+"]" );
         context.StatusMessage( "sp_trail_sub22=["+StringUtil.Substring( AV9V20, 2, 2)+"]" );
         context.StatusMessage( "sp_trail_sub13=["+StringUtil.Substring( AV9V20, 1, 3)+"]" );
         context.StatusMessage( "sp_trail_sub31=["+StringUtil.Substring( AV9V20, 3, 1)+"]" );
         context.StatusMessage( "re_empty_find=["+StringUtil.StringReplace( "abc", "", "X")+"]" );
         AV9V20 = "áéíóúçñü";
         context.StatusMessage( "acc_len="+StringUtil.Trim( StringUtil.Str( (decimal)(StringUtil.Len( AV9V20)), 10, 0)) );
         context.StatusMessage( "acc_sub22=["+StringUtil.Substring( AV9V20, 2, 2)+"]" );
         if ( StringUtil.StrCmp(StringUtil.Upper( AV9V20), "ÁÉÍÓÚÇÑÜ") == 0 )
         {
            context.StatusMessage( "acc_upper_eq=1" );
         }
         else
         {
            context.StatusMessage( "acc_upper_eq=0" );
         }
         if ( StringUtil.StrCmp(StringUtil.Lower( "ÁÉÍÓÚÇÑÜ"), "áéíóúçñü") == 0 )
         {
            context.StatusMessage( "acc_lower_eq=1" );
         }
         else
         {
            context.StatusMessage( "acc_lower_eq=0" );
         }
         context.StatusMessage( "sharp_upper=["+StringUtil.Upper( "ß")+"]" );
         context.StatusMessage( "sharp_lower=["+StringUtil.Lower( "ß")+"]" );
         context.StatusMessage( "micro_upper=["+StringUtil.Upper( "µ")+"]" );
         context.StatusMessage( "micro_lower=["+StringUtil.Lower( "µ")+"]" );
         context.StatusMessage( "yuml_upper=["+StringUtil.Upper( "ÿ")+"]" );
         context.StatusMessage( "yuml_lower=["+StringUtil.Lower( "Ÿ")+"]" );
         cleanup();
      }

      public override void cleanup( )
      {
         CloseCursors();
         if ( IsMain )
         {
            context.CloseConnections();
         }
         ExitApp();
      }

      public override void initialize( )
      {
         AV8C4 = "";
         AV9V20 = "";
         /* GeneXus formulas. */
      }

      private string AV8C4 ;
      private string AV9V20 ;
   }

}
