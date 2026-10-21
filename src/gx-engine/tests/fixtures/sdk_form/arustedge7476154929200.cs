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
   public class arustedge7476154929200 : GXProcedure
   {
      public static int Main( string[] args )
      {
         return new arustedge7476154929200().MainImpl(args); ;
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

      public arustedge7476154929200( )
      {
         context = new GxContext(  );
         DataStoreUtil.LoadDataStores( context);
         IsMain = true;
         context.SetDefaultTheme("KBTeste", true);
      }

      public arustedge7476154929200( IGxContext context )
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
         AV8Value = (long)(2+3*4);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((2+3)*4);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)(20-7-2);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)(48/ (decimal)(4)/ (decimal)(3));
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = -0;
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)(-(-7));
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)(99999999999999999L+1);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)(999999999999999999L-999999999999999998L);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)(7/ (decimal)(2));
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)(-7/ (decimal)(2));
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)(7/ (decimal)(-2));
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)(-7/ (decimal)(-2));
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)(1/ (decimal)(2));
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)(-1/ (decimal)(2));
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)(9/ (decimal)(4));
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         if ( -2 == 3 )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( -2 != 3 )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( -2 < 3 )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( -2 <= 3 )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( -2 > 3 )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( -2 >= 3 )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( 3 == 3 )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( 3 != 3 )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( 3 < 3 )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( 3 <= 3 )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( 3 > 3 )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( 3 >= 3 )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ( 0 == 1 ) && ( 0 == 1 ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ( 0 == 1 ) || ( 0 == 1 ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ( 0 == 1 ) && ( 1 == 1 ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ( 0 == 1 ) || ( 1 == 1 ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ( 1 == 1 ) && ( 0 == 1 ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ( 1 == 1 ) || ( 0 == 1 ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ( 1 == 1 ) && ( 1 == 1 ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ( 1 == 1 ) || ( 1 == 1 ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ( 1 == 1 ) || ( 1 == 2 ) && ( 2 == 3 ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ( ( 1 == 1 ) || ( 1 == 2 ) ) && ( 2 == 3 ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ! ( 1 == 2 ) && ! ( ( 3 < 2 ) || ( 4 != 4 ) ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ! ! ( 1 == 1 ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         if ( ! ( ( 1 == 2 ) ) && ( 3 == 3 ) || ( 4 == 5 ) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 0;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         AV9i = 0;
         while ( ( AV9i < 3 ) && ! ( AV9i == 2 ) )
         {
            AV9i = (long)(AV9i+1);
         }
         AV8Value = AV9i;
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV12x = 0;
         if ( ( AV12x != 0 ) && ( 10 /  ( decimal )( AV12x ) > Convert.ToDecimal( 1 )) )
         {
            AV8Value = 99;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         AV12x = 0;
         if ( ( AV12x == 0 ) || ( 10 /  ( decimal )( AV12x ) > Convert.ToDecimal( 1 )) )
         {
            AV8Value = 1;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         else
         {
            AV8Value = 99;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         AV12x = 123456789;
         AV8Value = (long)(AV12x*AV12x);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV12x = 7;
         AV11s = AV12x;
         AV12x = 9;
         AV8Value = AV11s;
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = AV12x;
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         if ( 2 < 3 )
         {
            if ( 5 == 6 )
            {
               AV8Value = 99;
               context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
            }
            else
            {
               AV8Value = 8;
               context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
            }
         }
         else
         {
            AV8Value = 77;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         AV9i = 5;
         while ( AV9i < 0 )
         {
            AV8Value = 99;
            context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         }
         AV8Value = AV9i;
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV9i = 0;
         AV11s = 0;
         while ( AV9i < 3 )
         {
            AV10j = 0;
            while ( AV10j < 4 )
            {
               AV11s = (long)(AV11s+AV9i*10+AV10j);
               AV10j = (long)(AV10j+1);
            }
            AV9i = (long)(AV9i+1);
         }
         AV8Value = AV11s;
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         if ( 1 == 1 )
         {
            AV12x = 7;
         }
         else
         {
            AV12x = 8;
         }
         AV8Value = AV12x;
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV13Number = 17;
         AV8Value = AV13Number;
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV12x = 3;
         AV12x = (long)(AV12x*AV12x);
         AV8Value = AV12x;
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((3129+-7197)*-486);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((692+-7418)*-3936);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((8164+-6902)*-1097);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((8633+-4425)*-7438);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((2647+-1893)*9379);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((2378+7683)*-1568);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((-8888+-7819)*-4842);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((-1137+7491)*9347);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((-5753+-5490)*-9227);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((3424+8605)*-2167);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((6370+3341)*-8410);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((-9963+8893)*-4088);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((2759+9783)*-4645);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((-8720+-5824)*-9108);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((-4386+-4505)*-610);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((2916+-7933)*3678);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((6654+3960)*-816);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((-2551+-9424)*-9900);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((5960+2174)*-8567);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((4341+7882)*-8086);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((1284+-3423)*-9146);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((-544+-7969)*4901);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((6846+440)*2782);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((5318+5699)*3231);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((8196+-2379)*1416);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((9411+9363)*1524);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((8661+-2577)*5750);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((56+-7207)*-1260);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((4461+6979)*-6214);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((7557+-4082)*3366);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((-8175+2995)*9876);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((-8044+-3539)*-1662);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((869+8629)*5400);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((-1727+827)*-9924);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((6966+-53)*1593);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((7218+-5669)*2718);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((7203+-9518)*6338);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((4871+6259)*2327);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((4566+7595)*-6144);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
         AV8Value = (long)((8095+-1935)*2731);
         context.StatusMessage( StringUtil.Trim( StringUtil.Str( (decimal)(AV8Value), 18, 0)) );
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

      private long AV8Value ;
      private long AV9i ;
      private long AV12x ;
      private long AV11s ;
      private long AV10j ;
      private long AV13Number ;
   }

}
