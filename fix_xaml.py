import sys
path = r'src\ExitBoost.App\MainWindow.xaml'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

search = '<CheckBox Content="Вся игра через VPN (стабильнее, но пинг как у выбранной локации)"'
idx = content.find(search)
if idx >= 0:
    print('Found at:', idx)
    # Find the end of this block
    end_marker = 'После смены маршрута игре может потребоваться переподключиться.'
    end_idx = content.find(end_marker, idx)
    if end_idx >= 0:
        end_idx += len(end_marker)
        print('Block length:', end_idx - idx)
        block = content[idx:end_idx]
        print('Block:', repr(block[:200]))
    else:
        print('End not found')
else:
    print('Search string not found')