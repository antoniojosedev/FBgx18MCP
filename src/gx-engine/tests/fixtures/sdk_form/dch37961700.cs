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
   public class dch37961700 : GXProcedure
   {
      public dch37961700( )
      {
         context = new GxContext(  );
         DataStoreUtil.LoadDataStores( context);
         IsMain = true;
         context.SetDefaultTheme("KBTeste", true);
      }

      public dch37961700( IGxContext context )
      {
         this.context = context;
         IsMain = false;
      }

      public void execute( ref decimal aP0_Hits ,
                           out decimal aP1_Result )
      {
         this.AV8Hits = aP0_Hits;
         this.AV9Result = 0 ;
         initialize();
         ExecuteImpl();
         aP0_Hits=this.AV8Hits;
         aP1_Result=this.AV9Result;
      }

      public decimal executeUdp( ref decimal aP0_Hits )
      {
         execute(ref aP0_Hits, out aP1_Result);
         return AV9Result ;
      }

      public void executeSubmit( ref decimal aP0_Hits ,
                                 out decimal aP1_Result )
      {
         this.AV8Hits = aP0_Hits;
         this.AV9Result = 0 ;
         SubmitImpl();
         aP0_Hits=this.AV8Hits;
         aP1_Result=this.AV9Result;
      }

      protected override void ExecutePrivate( )
      {
         /* GeneXus formulas */
         /* Output device settings */
         AV8Hits = (decimal)(AV8Hits+1);
         context.StatusMessage( "later_condition_helper_called="+StringUtil.Trim( StringUtil.Str( AV8Hits, 5, 2)) );
         AV9Result = (decimal)(1);
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
         /* GeneXus formulas. */
      }

      private decimal AV8Hits ;
      private decimal AV9Result ;
      private decimal aP0_Hits ;
      private decimal aP1_Result ;
   }

}
