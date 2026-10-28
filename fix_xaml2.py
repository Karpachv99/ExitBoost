path = r'src\ExitBoost.App\MainWindow.xaml'
with open(path, 'r', encoding='utf-8-sig') as f:
    lines = f.read().splitlines()

for i, line in enumerate(lines):
    # Find by Content attribute value (works even with encoding issues)
    if 'RouteGameThroughVpn' in line and '</CheckBox>' not in line:
        for j in range(i, min(i+6, len(lines))):
            if '</CheckBox>' in lines[j]:
                insert_pos = j + 1
                lines.insert(insert_pos, '                                     <TextBlock Style="{StaticResource Muted}" Text="IP/подсети игр через VPN (через запятую):" Margin="0,8,0,2" />')
                lines.insert(insert_pos+1, '                                     <TextBox Text="{Binding GameVpnEndpointsText, UpdateSourceTrigger=PropertyChanged}"')
                lines.insert(insert_pos+2, '                                              Margin="0,0,0,8" ToolTip="Сюда автоматически добавляются проблемные IP при срабатывании автообхода. Можно вписать вручную (например: 134.168.255.0/24, 1.2.3.4). Поддерживаются IP и CIDR." />')
                break
        break

with open(path, 'w', encoding='utf-8') as f:
    f.write('\r\n'.join(lines))
print('Done, inserted at', insert_pos)
