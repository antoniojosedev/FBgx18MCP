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
   public class artm41852400 : GXProcedure
   {
      public static int Main( string[] args )
      {
         return new artm41852400().MainImpl(args); ;
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

      public artm41852400( )
      {
         context = new GxContext(  );
         DataStoreUtil.LoadDataStores( context);
         IsMain = true;
         context.SetDefaultTheme("KBTeste", true);
      }

      public artm41852400( IGxContext context )
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
         AV8First = new SdtRSC79223900(context);
         AV8First.gxTpr_Label = " a b ";
         context.StatusMessage( "case0=["+StringUtil.Str( (decimal)(42), 10, 0)+"]" );
         context.StatusMessage( "case1=["+StringUtil.Str( (decimal)(-7), 10, 0)+"]" );
         context.StatusMessage( "case2=["+StringUtil.Str( 1.25m, 10, 0)+"]" );
         context.StatusMessage( "case3=["+StringUtil.Str( 1.5m, 10, 0)+"]" );
         context.StatusMessage( "case4=["+StringUtil.Str( -1.5m, 10, 0)+"]" );
         context.StatusMessage( "case5=["+StringUtil.Str( (decimal)(9999999999L), 10, 0)+"]" );
         context.StatusMessage( "case6=["+StringUtil.Str( (decimal)(10000000000L), 10, 0)+"]" );
         context.StatusMessage( "case7=["+StringUtil.Str( 1.25m, 6, 2)+"]" );
         context.StatusMessage( "case8=["+StringUtil.Str( 9.99m, 3, 1)+"]" );
         context.StatusMessage( "case9=["+StringUtil.Str( 9.99m, 3, 0)+"]" );
         context.StatusMessage( "case10=["+StringUtil.Trim( "  a b  ")+"]" );
         context.StatusMessage( "case11=["+StringUtil.Trim( AV8First.gxTpr_Label)+"]" );
         context.StatusMessage( "case12=["+StringUtil.Trim( " 	a	 ")+"]" );
         context.StatusMessage( "case13=["+StringUtil.Trim( " a ")+"]" );
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
         AV8First = new SdtRSC79223900(context);
         /* GeneXus formulas. */
      }

      private SdtRSC79223900 AV8First ;
   }

}
