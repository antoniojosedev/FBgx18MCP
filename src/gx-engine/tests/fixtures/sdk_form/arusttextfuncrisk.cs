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
   public class arusttextfuncrisk : GXProcedure
   {
      public static int Main( string[] args )
      {
         return new arusttextfuncrisk().MainImpl(args); ;
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

      public arusttextfuncrisk( )
      {
         context = new GxContext(  );
         DataStoreUtil.LoadDataStores( context);
         IsMain = true;
         context.SetDefaultTheme("KBTeste", true);
      }

      public arusttextfuncrisk( IGxContext context )
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
         AV9V10 = "abc";
         AV8C10 = "ABCDEFGHIJ";
         context.StatusMessage( "o_v10_len_decl=["+StringUtil.Substring( AV9V10, 1, 10)+"]" );
         context.StatusMessage( "o_v10_len_over=["+StringUtil.Substring( AV9V10, 2, 5)+"]" );
         context.StatusMessage( "o_v10_start_end=["+StringUtil.Substring( AV9V10, 4, 2)+"]" );
         context.StatusMessage( "o_c10_len_over=["+StringUtil.Substring( AV8C10, 8, 5)+"]" );
         context.StatusMessage( "o_c10_len_decl_over=["+StringUtil.Substring( AV8C10, 1, 11)+"]" );
         AV9V10 = "ABCDEFGHIJ";
         context.StatusMessage( "o_v10_full_end=["+StringUtil.Substring( AV9V10, 10, 1)+"]" );
         context.StatusMessage( "o_v10_full_len_over=["+StringUtil.Substring( AV9V10, 8, 5)+"]" );
         context.StatusMessage( "o_v10_len_0=["+StringUtil.Substring( AV9V10, 2, 0)+"]" );
         context.StatusMessage( "o_v10_start_gt=["+StringUtil.Substring( AV9V10, 11, 1)+"]" );
         context.StatusMessage( "o_m_v10_start_gt=["+StringUtil.Substring( AV9V10, 11, 1)+"]" );
         context.StatusMessage( "o_v10_start_0=["+StringUtil.Substring( AV9V10, 0, 2)+"]" );
         context.StatusMessage( "o_v10_start_neg=["+StringUtil.Substring( AV9V10, -1, 2)+"]" );
         context.StatusMessage( "o_v10_len_neg=["+StringUtil.Substring( AV9V10, 2, -1)+"]" );
         context.StatusMessage( "o_c10_start_gt=["+StringUtil.Substring( AV8C10, 11, 1)+"]" );
         context.StatusMessage( "o_c10_start_0=["+StringUtil.Substring( AV8C10, 0, 3)+"]" );
         context.StatusMessage( "o_c10_start_neg=["+StringUtil.Substring( AV8C10, -1, 3)+"]" );
         AV9V10 = "";
         context.StatusMessage( "o_v10_empty_1_1=["+StringUtil.Substring( AV9V10, 1, 1)+"]" );
         context.StatusMessage( "o_v10_empty_len0=["+StringUtil.Substring( AV9V10, 1, 0)+"]" );
         context.StatusMessage( "o_replace_empty_find=["+StringUtil.StringReplace( AV9V10, "", "X")+"]" );
         context.StatusMessage( "o_replace_empty_with=["+StringUtil.StringReplace( "abc", "b", "")+"]" );
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
         AV9V10 = "";
         AV8C10 = "";
         /* GeneXus formulas. */
      }

      private string AV8C10 ;
      private string AV9V10 ;
   }

}
