using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using DevProxyApp;
class ResizeChecks {
 const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
 static int checks;
 static void Assert(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
 static bool Buffered(Control c){return (bool)typeof(Control).GetProperty("DoubleBuffered",Private).GetValue(c,null);}
 static bool ResizeRedraw(Control c){return (bool)typeof(Control).GetMethod("GetStyle",Private).Invoke(c,new object[]{ControlStyles.ResizeRedraw});}
 static void Walk(Control c,List<Control> all){all.Add(c);foreach(Control child in c.Controls)Walk(child,all);}
 static void Snapshot(Form f,string name){using(var bitmap=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(bitmap,new Rectangle(Point.Empty,f.Size));bitmap.Save(name);}}
 [STAThread]static int Main(){
  MainForm form=null;
  try{
   Application.EnableVisualStyles();form=new MainForm();form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
   var all=new List<Control>();Walk(form,all);
   Assert(Buffered(form)&&ResizeRedraw(form),"Main window does not redraw on resize");
   Label offer=null;var cards=new List<Control>();var dirty=new Dictionary<Control,bool>();
   foreach(var c in all){
    if(c.GetType().Name=="FluentCard"){
     cards.Add(c);Assert(Buffered(c)&&ResizeRedraw(c),"Card does not fully redraw on resize");
     dirty[c]=false;var captured=c;
     c.Invalidated+=(sender,e)=>{if(e.InvalidRect.Contains(captured.ClientRectangle))dirty[captured]=true;};
    }
    if(c.GetType().Name=="FluentLayout"||c.GetType().Name=="FluentPageHost")Assert(Buffered(c)&&ResizeRedraw(c),"Layout host is not buffered");
    if(c is ListView)Assert(Buffered(c),"Native app list is not buffered");
    if(c is Label&&c.Text.Contains("DAGAMI"))offer=(Label)c;
   }
   Assert(offer!=null&&offer.Text.Contains("Получить прокси")&&offer.Text.Contains("Кодовое слово"),"Bot explanation missing");
   for(int i=0;i<16;i++){
    foreach(var card in cards)dirty[card]=false;
    form.ClientSize=i%2==0?new Size(1360,920):new Size(1020,730);Application.DoEvents();form.Update();
    foreach(var card in cards)if(card.Visible)Assert(dirty[card],"Visible card did not invalidate its whole surface on resize");
   }
   form.Size=form.MinimumSize;Application.DoEvents();
   Assert(offer.Visible&&offer.Bottom<=offer.Parent.ClientSize.Height,"Bot explanation clipped at minimum size");
   var measured=TextRenderer.MeasureText(offer.Text,offer.Font,new Size(offer.Width,1000),TextFormatFlags.WordBreak);
   Assert(measured.Height<=offer.Height,"Bot explanation text does not fit");Snapshot(form,"resize-minimum.png");
   form.ClientSize=new Size(1440,980);Application.DoEvents();Snapshot(form,"resize-expanded.png");
   form.WindowState=FormWindowState.Maximized;Application.DoEvents();Assert(form.WindowState==FormWindowState.Maximized,"Maximize failed");
   form.WindowState=FormWindowState.Normal;Application.DoEvents();Snapshot(form,"resize-restored.png");
   Console.WriteLine("PASS "+checks+" repeated resize, full invalidation, buffering, maximize/restore and bot caption checks");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  finally{if(form!=null){typeof(MainForm).GetMethod("ExitApplication",Private).Invoke(form,null);form.Dispose();}}
 }
}
