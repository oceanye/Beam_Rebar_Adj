   M S B A M L   `   `   `  ����  SEI_Beam_Rebar_Adj$    TeklaRebarsAdjuster.MainWindow   5   	   W SPresentationCore, Version=8.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\ XPresentationFramework, Version=8.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35e aSystem.Windows.Controls.Ribbon, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089R NWindowsBase, Version=8.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35F 9http://schemas.microsoft.com/winfx/2006/xaml/presentation     5   	   R NSystem.Xaml, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089:x,http://schemas.microsoft.com/winfx/2006/xaml     5   	     � Title$$  Tekla 钢筋端点调整工具��5   	   $	��300��6    $	��400��6-    � WindowStartupLocation$ CenterScreen=�5   	   .��5      �  � RowDefinitions 5   
   �� 5      $��1* �6   �� 5	      $��3* �6   �� 5
      $��1* �6   .+�5   
   �� $��0��6    �� Orientation$ 
Horizontal=�6"   $��Center=�6;   .+�5      �� -   5         ��Name$ SelectRebarsButton��5      $��选择钢筋��61   $��5q�6@   $��	10,5,10,5q�6K   $��80��6_   6m   �� 5      -   5        $ SelectPointButton��5      $��	选择点��60   $��5q�6>   $��	10,5,10,5q�6I   $��80��6]   ${�False��6�   �� 5      -   5        $ AdjustEndpointsButton��5      $��调整端点��64   $��5q�6C   $��	10,5,10,5q�6N   $��80��6b   ${�False��6�   �� 5      -   5        $ ResetButton��5      $��重置��6*   $��5q�67   $��	10,5,10,5q�6B   $��80��6V   6d   5      �� 5   
   -   5         ��Name$ StatusScrollViewer��5   0   $��1��6   $��10q�6$   .��5      �� -   5         ��Name$ 
StatusText��5      $
��Wrap=�6,   $��5q�6@   $��Consolas)�6K   5      �� 5   
   $��2��6   
�������
�������$��0,1,0,0q�6L   ._�5      �� $��准备就绪��6   $��	10,5,10,5q�6$   $
��Left=�67   $��Center=�6R   5      5      5      