using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Obstruo.UI
{
    public partial class SplashWindow : Window
    {
        private bool _dismissing;

        public event EventHandler? SplashCompleted;

        public SplashWindow()
        {
            InitializeComponent();
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            Focus();
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            Dismiss();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            Dismiss();
        }

        private void Dismiss()
        {
            if (_dismissing) return;
            _dismissing = true;
            ((Storyboard)Resources["FadeOut"]).Begin();
        }

        private void FadeOut_Completed(object sender, EventArgs e)
        {
            SplashCompleted?.Invoke(this, EventArgs.Empty);
            Close();
        }
    }
}