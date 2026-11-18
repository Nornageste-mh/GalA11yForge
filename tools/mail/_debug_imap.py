import imaplib, sys
sys.path.insert(0, r"D:\DSHWorkSpace\tools\mail")
import mail163

pw = mail163.read_pass()
c = imaplib.IMAP4_SSL("imap.163.com", 993)
id_cmd = '("name" "GalA11yForge" "version" "0.1" "vendor" "dsh")'
print("id:", c._simple_command("ID", id_cmd))
print("untagged:", c.untagged_responses)
print("login:", c.login("18357873105@163.com", pw))
print("select:", c.select("INBOX"))
print("search:", c.search(None, "ALL"))
c.logout()
