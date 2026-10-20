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
   public class odh42412500 : GXProcedure
   {
      public odh42412500( )
      {
         context = new GxContext(  );
         DataStoreUtil.LoadDataStores( context);
         IsMain = true;
         context.SetDefaultTheme("KBTeste", true);
      }

      public odh42412500( IGxContext context )
      {
         this.context = context;
         IsMain = false;
      }

      public void execute( out DateTime aP0_Stamp )
      {
         this.AV8Stamp = DateTime.MinValue ;
         initialize();
         ExecuteImpl();
         aP0_Stamp=this.AV8Stamp;
      }

      public DateTime executeUdp( )
      {
         execute(out aP0_Stamp);
         return AV8Stamp ;
      }

      public void executeSubmit( out DateTime aP0_Stamp )
      {
         this.AV8Stamp = DateTime.MinValue ;
         SubmitImpl();
         aP0_Stamp=this.AV8Stamp;
      }

      protected override void ExecutePrivate( )
      {
         /* GeneXus formulas */
         /* Output device settings */
         context.StatusMessage( "datetime_entry_empty="+((DateTime.MinValue==AV8Stamp) ? "true" : "false") );
         context.StatusMessage( "datetime_entry_year="+StringUtil.Trim( StringUtil.Str( (decimal)(DateTimeUtil.Year( AV8Stamp)), 10, 0)) );
         AV8Stamp = context.localUtil.YMDHMSToT( 2024, 2, 29, 12, 34, 56);
         context.StatusMessage( "datetime_after_empty="+((DateTime.MinValue==AV8Stamp) ? "true" : "false") );
         context.StatusMessage( "datetime_after_year="+StringUtil.Trim( StringUtil.Str( (decimal)(DateTimeUtil.Year( AV8Stamp)), 10, 0)) );
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
         AV8Stamp = (DateTime)(DateTime.MinValue);
         /* GeneXus formulas. */
      }

      private DateTime AV8Stamp ;
      private DateTime aP0_Stamp ;
   }

}
