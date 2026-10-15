using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DevProxyApp;
class CompactChecks {
 const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 static MainForm form;static int checks;
 [DllImport("user32.dll",EntryPoint="GetWindowLongW")]static extern int GetWindowLong(IntPtr window,int index);
 static void Assert(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
 static object Get(string name){return typeof(MainForm).GetField(name,Private).GetValue(form);}
 static void Call(string name,params object[] args){typeof(MainForm).GetMethod(name,Private).Invoke(form,args);Application.DoEvents();}
 static void Layout(Control c){foreach(Control child in c.Controls)if(child.Visible){Assert(child.Left>=0&&child.Top>=0&&child.Right<=c.ClientSize.Width+2&&child.Bottom<=c.ClientSize.Height+2,"Clipped: "+child.GetType().Name+" "+child.Text+" "+child.Bounds+" in "+c.ClientSize);Layout(child);}}
 static void Snapshot(string name){using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(name);}}
 static void CheckList(){
  var list=(ListView)Get("_targetList");
  int count=Math.Min(6,list.Items.Count);Assert(count>0,"Empty app list");
  Assert(list.GetItemRect(count-1).Bottom<=list.ClientSize.Height,"First six applications are clipped: list height="+list.Height+" client="+list.ClientSize+" cols="+list.Columns[0].Width+","+list.Columns[1].Width+","+list.Columns[2].Width+" last row="+list.GetItemRect(count-1));
  Assert((GetWindowLong(list.Handle,-16)&0x00100000)==0,"List has unnecessary horizontal scrollbar");
 }
 [STAThread]static int Main(){
  try{
   Application.EnableVisualStyles();form=new MainForm();form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
   Assert(form.WindowState==FormWindowState.Normal,"App starts maximized");
   Assert(form.ClientSize.Width<=1080&&form.ClientSize.Height<=720,"Default window is not compact");
   Console.WriteLine("Default outer="+form.Size+" client="+form.ClientSize+" list="+((ListView)Get("_targetList")).Bounds);Snapshot("compact-default.png");Layout(form);CheckList();
   form.Size=form.MinimumSize;Application.DoEvents();Layout(form);CheckList();Snapshot("compact-minimum.png");
   foreach(int page in new[]{1,2,3}){Call("ShowFluentPage",page);Layout(form);Snapshot("compact-page-"+page+".png");}
   Call("ShowFluentPage",0);
   form.ClientSize=new Size(1350,800);Application.DoEvents();CheckList();
   form.Size=form.MinimumSize;Application.DoEvents();CheckList();
   form.WindowState=FormWindowState.Maximized;Application.DoEvents();form.Hide();Call("ShowNativeWindow");Assert(form.WindowState==FormWindowState.Maximized,"Tray activation discards manual maximization");
   form.WindowState=FormWindowState.Normal;Application.DoEvents();form.Hide();Call("ShowNativeWindow");Assert(form.WindowState==FormWindowState.Normal,"Tray activation maximizes normal window");
   Console.WriteLine("PASS "+checks+" compact default/minimum layout, six visible rows, no horizontal scrolling, navigation and tray size checks");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  finally{if(form!=null){Call("ExitApplication");form.Dispose();}}
 }
}
