"""Unix DNS dialog; all system operations run outside Tk's UI thread."""
import queue
import threading
import tkinter as tk
from tkinter import ttk, messagebox
from . import system_dns


def confirm_reset(ask):
    question = 'Вы точно уверены в том что вы делаете?'
    if not ask('Полный сброс — 1 из 2', question+'\n\nDNS всех найденных подключений вернётся в авто. Правила DNS других программ тоже входят в сброс. Сеть и рабочие имена могут стать недоступны.'):
        return False
    return ask('Полный сброс — 2 из 2', question+'\n\nСписки и галочки не ограничивают сброс. До записи сохраним копию. Нажатие «Да» запросит права администратора.')


class DnsDialog(tk.Toplevel):
    def __init__(self, parent, system=None, execute=None):
        super().__init__(parent)
        self.title('Dagami Agent Proxy — DNS Linux / macOS')
        self.geometry('850x650'); self.minsize(780, 610)
        self.iconphoto(True, parent.app_icon)
        self.busy = False; self.events = queue.Queue(); self.system = system
        self.execute = execute or system_dns.execute
        self.server = tk.StringVar(value='185.149.145.6'); self.mode = tk.StringVar(value='domains')
        self.preset = tk.StringVar(value='Development'); self.replace = tk.BooleanVar()
        body = ttk.Frame(self, padding=18); body.pack(fill='both', expand=True); body.columnconfigure(1, weight=1); body.rowconfigure(8, weight=1)
        ttk.Label(body, text='Для доменов — отдельные правила. Общий DNS подключений меняется только в отдельном режиме.\nПеред записью и после неё проверим DNS и HTTPS. При сбое — откат. Права запрашивает ОС.', wraplength=790).grid(row=0, column=0, columnspan=2, sticky='ew', pady=(0,12))
        ttk.Label(body,text='DNS-сервер (IP)').grid(row=1,column=0,sticky='w'); ttk.Entry(body,textvariable=self.server).grid(row=1,column=1,sticky='ew',pady=4)
        ttk.Label(body,text='Набор доменов').grid(row=2,column=0,sticky='w'); ttk.Combobox(body,textvariable=self.preset,values=['Development','Full'],state='readonly').grid(row=2,column=1,sticky='ew',pady=4)
        modes=ttk.Frame(body); modes.grid(row=3,column=0,columnspan=2,sticky='ew',pady=4)
        ttk.Radiobutton(modes,text='Только домены',variable=self.mode,value='domains').pack(side='left')
        ttk.Radiobutton(modes,text='Общий DNS выбранных подключений',variable=self.mode,value='adapters').pack(side='left',padx=12)
        ttk.Checkbutton(body,text='Разрешить замену конфликтующих правил с копией',variable=self.replace).grid(row=4,column=0,columnspan=2,sticky='w')
        self.services=ttk.Treeview(body,columns=('name',),show='headings',selectmode='extended',height=5);self.services.heading('name',text='Подключения (для общего DNS выберите нужные строки)');self.services.column('name',width=700)
        self.services.grid(row=5,column=0,columnspan=2,sticky='nsew',pady=8)
        actions=ttk.Frame(body);actions.grid(row=6,column=0,columnspan=2,sticky='ew')
        self.buttons=[]
        for text,action,style in [('Записать DNS в систему',lambda:self.perform('apply'),'Accent.TButton'),('Восстановить прежний DNS',lambda:self.perform('restore'),'Accent.TButton'),('Обновить',self.refresh,'TButton')]:
            button=ttk.Button(actions,text=text,command=action,style=style);button.pack(side='left',padx=(0,6));self.buttons.append(button)
        reset=ttk.Button(body,text='Очистить все DNS и правила…',style='Danger.TButton',command=self.reset);reset.grid(row=7,column=0,columnspan=2,sticky='w',pady=10);self.buttons.append(reset)
        self.report=tk.Text(body,wrap='word',height=9,state='disabled');self.report.grid(row=8,column=0,columnspan=2,sticky='nsew')
        self.protocol('WM_DELETE_WINDOW',self.close);self.pump_job=self.after(100,self.pump);self.refresh();self.transient(parent);self.grab_set()

    def message(self,text):
        self.report.configure(state='normal');self.report.delete('1.0','end');self.report.insert('1.0',text);self.report.configure(state='disabled')

    def work(self,operation,kind):
        if self.busy:return
        self.busy=True
        for button in self.buttons:button.configure(state='disabled')
        self.message('Чтение подключений…' if kind=='list' else 'Запрос прав и работа с DNS. Дождитесь отчёта.')
        def worker():
            try:self.events.put((kind,True,operation()))
            except Exception as exc:self.events.put((kind,False,str(exc)))
        threading.Thread(target=worker,daemon=True).start()

    def refresh(self):
        def read():
            if self.system is None:self.system=system_dns.backend()
            return self.system.services()
        self.work(read,'list')

    def perform(self,action,confirmed=False):
        values={'server':self.server.get().strip(),'mode':self.mode.get(),'preset':self.preset.get(),'replace':self.replace.get(),'services':list(self.services.selection()),'confirmed':confirmed}
        # Validate captured inputs before requesting privileges.
        try:system_dns.validate_request(dict(values,action=action,job='0'*32))
        except Exception as exc:self.message(str(exc));return
        self.work(lambda:self.execute(action,**values),'operation')

    def reset(self):
        if self.busy:return
        if confirm_reset(lambda title,text:messagebox.askyesno(title,text,parent=self,default='no',icon='warning')):self.perform('reset',True)

    def pump(self):
        try:
            while True:
                kind,ok,value=self.events.get_nowait();self.busy=False
                for button in self.buttons:button.configure(state='normal')
                if kind=='list' and ok:
                    self.services.delete(*self.services.get_children())
                    for item in value:self.services.insert('', 'end', iid=item['id'], values=(item['name'],))
                    self.message('Готово. Режим по умолчанию — только домены. Для общего DNS выберите подключения.\nLinux: режим доменов требует уже настроенный NetworkManager/dnsmasq; другие режимы DNS не включаются автоматически.')
                else:self.message(str(value))
        except queue.Empty:pass
        self.pump_job=self.after(100,self.pump)

    def close(self):
        if self.busy:messagebox.showinfo('DNS','Дождитесь завершения операции и отчёта.',parent=self);return
        self.destroy()

    def destroy(self):
        if getattr(self,'pump_job',None):self.after_cancel(self.pump_job);self.pump_job=None
        super().destroy()
